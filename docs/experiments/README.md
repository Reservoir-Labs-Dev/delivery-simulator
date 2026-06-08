# Experiment runner — DOG-52

`scripts/run-experiment.sh` drives one labelled chaos experiment end-to-end and
captures everything needed to write up the result. The same script runs the
baseline and every chaos scenario (DOG-53..DOG-57) — only the CLI args change.

## What it does

1. Health-checks `order-service` and `dashboard-api`.
2. Resets every row in `chaos.chaos_config` to disabled, then enables the
   target scenario via the dashboard-api PATCH endpoint.
3. Records `run_start_utc` — the lower bound of the metrics window.
4. POSTs `--orders` orders, optionally paced by `--rate` (orders/sec).
5. Waits for the pipeline to drain. "Drained" = no new row landed in
   `metrics.metrics` (filtered to the run window) for `DRAIN_IDLE_SEC`
   consecutive seconds, or `DRAIN_TIMEOUT_SEC` total elapsed.
6. Records `run_end_utc`, disables chaos.
7. Dumps the metric rows in `[run_start_utc, run_end_utc]` to
   `docs/experiments/runs/<experiment-id>-<runtag>/metrics.csv` and writes a
   `summary.txt` with per-service outcome counts, p50/p95 durations, retry
   distribution, and DLQ-bound count.

Window-based slicing means we don't need an `experiment_id` column in the
metrics table — runs are isolated by start/end timestamps + the per-run output
folder.

## Usage

```bash
# Baseline (no chaos)
scripts/run-experiment.sh \
  --experiment-id exp1-baseline \
  --orders 50 \
  --scenario none

# Chaos scenario with params
scripts/run-experiment.sh \
  --experiment-id exp2-payment-delay \
  --orders 50 \
  --scenario delayed_payment \
  --params '{"delay_ms":5000}'

# Throttled rate
scripts/run-experiment.sh \
  --experiment-id exp3-burst \
  --orders 100 \
  --scenario duplicate_events \
  --rate 5
```

### CLI args

| Flag | Default | Meaning |
| --- | --- | --- |
| `--experiment-id` | (required) | Goes in the run-dir name and summary header. |
| `--orders` | `50` | Number of orders to POST. |
| `--scenario` | `none` | Chaos scenario key — must match a row in `chaos.chaos_config`. |
| `--params` | `{}` | JSON blob written to `chaos.chaos_config.params`. |
| `--rate` | unbounded | Orders/sec. Computed via `awk` as `1/rate` sleep between POSTs. |

### Env overrides

`ORDER_API`, `DASHBOARD_API`, `PG_CONTAINER`, `PG_USER`, `PG_DB`,
`DRAIN_IDLE_SEC` (default 5), `DRAIN_TIMEOUT_SEC` (default 180),
`OUT_ROOT` (default `docs/experiments/runs`).

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Run completed; CSV dumped. (Interpretation is the analyst's job.) |
| `1` | Usage error or preflight failure. |
| `2` | Drain timed out before the pipeline went idle. |

## Per-run output

```
docs/experiments/runs/<experiment-id>-<runtag>/
  metrics.csv     # raw rows, schema matches GET /metrics/export.csv
  summary.txt     # per-service counts + p50/p95 + retry + DLQ-bound
  run.log         # the full script stdout, tee'd
```

`metrics.csv` columns:
`id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`

## Prerequisites

- `docker compose up -d` is running and every service is healthy.
- `curl`, `awk`, `docker`, `psql` (via `docker exec`) are available on the host.
- The dashboard-api `chaos.chaos_config` table is seeded — `dashboard-api`
  bootstraps it on startup, so just bringing the stack up is enough.
