import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { listChaos, setChaos } from './chaosApi';

// DOG-49 — Chaos Control Panel.
//
// Renders one card per chaos scenario the system supports. Each card has:
//   - an enabled/disabled toggle
//   - (optional) one numeric input that maps to the scenario's single param
//     ({delay_ms}, {count}, {factor} — the conventions established by
//     ChaosAware* decorators in DOG-44/46/47)
//
// State is polled every POLL_INTERVAL_MS so a toggle made in another tab or
// directly via psql is reflected here without manual refresh.
//
// `broker_restart` is intentionally not a row in chaos.chaos_config — there
// is no consumer-side decorator for it. The operator triggers it manually
// via `scripts/chaos-broker-restart.sh` (DOG-48). We render an
// informational-only card so the panel still documents the full M3 chaos
// surface in one place.

const POLL_INTERVAL_MS = 5000;

// Canonical metadata for every scenario shown in the panel. Keep `name`
// in sync with each decorator's ChaosScenarios.* constant.
const SCENARIOS = [
  {
    name: 'delayed_payment',
    label: 'Delayed payment',
    service: 'PaymentService',
    description:
      'Sleeps the payment simulator by N ms before charging — models a slow PSP.',
    paramKey: 'delay_ms',
    paramLabel: 'delay_ms',
    paramDefault: 5000,
    paramMin: 0,
    paramMax: 60000,
    paramSuffix: 'ms',
  },
  {
    name: 'delivery_failure_loop',
    label: 'Delivery failure loop',
    service: 'DeliveryService',
    description:
      'Throws on every delivery attempt — drives 3 retries then routes to delivery.dlq.',
    paramKey: null,
  },
  {
    name: 'duplicate_events',
    label: 'Duplicate events',
    service: 'OrderService',
    description:
      'Publishes each order.created N times — exercises consumer idempotency.',
    paramKey: 'count',
    paramLabel: 'count',
    paramDefault: 3,
    paramMin: 2,
    paramMax: 10,
    paramSuffix: '×',
  },
  {
    name: 'kitchen_slowdown',
    label: 'Kitchen slowdown',
    service: 'KitchenService',
    description:
      'Multiplies kitchen prep time by N — backs up the pipeline downstream.',
    paramKey: 'factor',
    paramLabel: 'factor',
    paramDefault: 10,
    paramMin: 2,
    paramMax: 50,
    paramSuffix: '×',
  },
];

// Informational-only card. Renders alongside the toggleable scenarios but
// has no DB row and no toggle — the operator triggers it manually.
const BROKER_RESTART_CARD = {
  name: 'broker_restart',
  label: 'Broker restart survival',
  service: 'RabbitMQ',
  description:
    'Hard-bounces the broker mid-flow; durable queues + persistent messages mean every in-flight order still reaches COMPLETED.',
  command: 'bash scripts/chaos-broker-restart.sh',
};

function parseParams(paramsString) {
  if (!paramsString) return {};
  try {
    const v = JSON.parse(paramsString);
    return typeof v === 'object' && v !== null ? v : {};
  } catch {
    return {};
  }
}

function ChaosPanel() {
  // Map of name -> { enabled, params (object) } — single source of truth for
  // the controls. Initialised optimistically from SCENARIOS defaults so the
  // panel renders before the first poll completes.
  const [state, setState] = useState(() => {
    const seed = {};
    for (const s of SCENARIOS) {
      seed[s.name] = {
        enabled: false,
        params: s.paramKey ? { [s.paramKey]: s.paramDefault } : {},
      };
    }
    return seed;
  });
  const [pending, setPending] = useState({});  // name -> bool: in-flight write
  const [error, setError] = useState(null);
  const [lastSync, setLastSync] = useState(null);

  // Skip overwriting freshly-toggled rows on the next poll. We bump this
  // when a write succeeds so the optimistic UI doesn't flicker.
  const skipMergeRef = useRef(new Set());

  const refresh = useCallback(async (signal) => {
    try {
      const rows = await listChaos(signal);
      setState((prev) => {
        const next = { ...prev };
        for (const row of rows) {
          if (skipMergeRef.current.has(row.name)) continue;
          if (!next[row.name]) continue; // unknown scenario — ignore
          next[row.name] = {
            enabled: !!row.enabled,
            params: parseParams(row.params),
          };
        }
        return next;
      });
      skipMergeRef.current.clear();
      setLastSync(new Date());
      setError(null);
    } catch (err) {
      if (err.name === 'AbortError') return;
      setError(err.message || String(err));
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    refresh(ctrl.signal);
    const id = setInterval(() => refresh(ctrl.signal), POLL_INTERVAL_MS);
    return () => {
      ctrl.abort();
      clearInterval(id);
    };
  }, [refresh]);

  const applyChange = useCallback(async (scenario, nextEnabled, nextParams) => {
    setPending((p) => ({ ...p, [scenario.name]: true }));
    setState((prev) => ({
      ...prev,
      [scenario.name]: { enabled: nextEnabled, params: nextParams },
    }));
    skipMergeRef.current.add(scenario.name);
    try {
      await setChaos(scenario.name, nextEnabled, nextParams);
      setError(null);
    } catch (err) {
      setError(err.message || String(err));
    } finally {
      setPending((p) => {
        const copy = { ...p };
        delete copy[scenario.name];
        return copy;
      });
    }
  }, []);

  const activeScenarios = useMemo(
    () => SCENARIOS.filter((s) => state[s.name]?.enabled).map((s) => s.label),
    [state],
  );

  return (
    <section className="chaos" aria-labelledby="chaos-heading">
      <div className="chaos-header">
        <div>
          <div className="chaos-meta">DOG-49 · chaos control</div>
          <h2 id="chaos-heading" className="chaos-title">Chaos panel</h2>
        </div>
        <div className="chaos-sync">
          {error ? (
            <span className="chaos-sync-err" title={error}>sync error</span>
          ) : lastSync ? (
            <span className="chaos-sync-ok">
              synced {lastSync.toLocaleTimeString(undefined, { hour12: false })}
            </span>
          ) : (
            <span className="chaos-sync-pending">loading…</span>
          )}
        </div>
      </div>

      {activeScenarios.length > 0 && (
        <div className="chaos-banner" role="alert">
          <span className="chaos-banner-dot" aria-hidden="true" />
          <strong>Chaos active:</strong>&nbsp;
          {activeScenarios.join(' · ')}
        </div>
      )}

      <div className="chaos-grid">
        {SCENARIOS.map((scenario) => {
          const row = state[scenario.name] || { enabled: false, params: {} };
          const inFlight = !!pending[scenario.name];
          const paramValue = scenario.paramKey
            ? row.params[scenario.paramKey] ?? scenario.paramDefault
            : null;
          return (
            <div
              key={scenario.name}
              className={`chaos-card ${row.enabled ? 'chaos-card--on' : ''}`}
            >
              <div className="chaos-card-head">
                <div>
                  <div className="chaos-card-name">{scenario.label}</div>
                  <div className="chaos-card-service">{scenario.service}</div>
                </div>
                <label className="chaos-toggle" title="enable/disable">
                  <input
                    type="checkbox"
                    checked={row.enabled}
                    disabled={inFlight}
                    onChange={(e) =>
                      applyChange(scenario, e.target.checked, row.params)
                    }
                  />
                  <span className="chaos-toggle-track" aria-hidden="true">
                    <span className="chaos-toggle-thumb" />
                  </span>
                </label>
              </div>

              <div className="chaos-card-desc">{scenario.description}</div>

              {scenario.paramKey && (
                <div className="chaos-card-param">
                  <label
                    className="chaos-param-label"
                    htmlFor={`chaos-${scenario.name}-param`}
                  >
                    {scenario.paramLabel}
                  </label>
                  <div className="chaos-param-input-wrap">
                    <input
                      id={`chaos-${scenario.name}-param`}
                      className="chaos-param-input"
                      type="number"
                      min={scenario.paramMin}
                      max={scenario.paramMax}
                      step={1}
                      value={paramValue}
                      disabled={inFlight}
                      onChange={(e) => {
                        const raw = e.target.value;
                        const next = raw === '' ? '' : Number(raw);
                        setState((prev) => ({
                          ...prev,
                          [scenario.name]: {
                            ...prev[scenario.name],
                            params: { [scenario.paramKey]: next },
                          },
                        }));
                      }}
                      onBlur={(e) => {
                        let v = Number(e.target.value);
                        if (Number.isNaN(v)) v = scenario.paramDefault;
                        v = Math.max(scenario.paramMin, Math.min(scenario.paramMax, v));
                        const nextParams = { [scenario.paramKey]: v };
                        applyChange(scenario, row.enabled, nextParams);
                      }}
                    />
                    <span className="chaos-param-suffix">
                      {scenario.paramSuffix}
                    </span>
                  </div>
                </div>
              )}
            </div>
          );
        })}

        <div className="chaos-card chaos-card--info">
          <div className="chaos-card-head">
            <div>
              <div className="chaos-card-name">{BROKER_RESTART_CARD.label}</div>
              <div className="chaos-card-service">{BROKER_RESTART_CARD.service}</div>
            </div>
            <span className="chaos-card-badge">manual</span>
          </div>
          <div className="chaos-card-desc">{BROKER_RESTART_CARD.description}</div>
          <code className="chaos-card-cmd">{BROKER_RESTART_CARD.command}</code>
        </div>
      </div>
    </section>
  );
}

export default ChaosPanel;
