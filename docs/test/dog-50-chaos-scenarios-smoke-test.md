# DOG-50 — Chaos scenarios smoke test

**Status:** Verification procedure + automated harness
**Linked issue:** DOG-50 (M3 — Chaos Engine, manual smoke test)
**Depends on:** DOG-44 (delayed_payment), DOG-45 (delivery_failure_loop), DOG-46 (duplicate_events), DOG-47 (kitchen_slowdown), DOG-48 (broker_restart), DOG-49 (control panel)
**Harness:** `scripts/chaos-smoke-tests.sh`

---

## 1. Goal

Prove, in one runnable suite, that every M3 chaos scenario actually fires when its `chaos.chaos_config` row is flipped on. This is the canonical "all chaos works" evidence the thesis Results chapter cites — the four decorator-based scenarios (DOG-44/45/46/47) plus the manual broker bounce (DOG-48). DOG-49 ships the panel that toggles them; DOG-50 proves the toggles do what they advertise.

No new service code — this ticket is a verification + reporting layer over what M3 already built.

| Scenario | Decorator | Oracle |
|---|---|---|
| `delayed_payment` | `services/payment/Chaos/ChaosAwarePaymentSimulator.cs` | payment-service logs `Chaos [delayed_payment] active` once per order; every order still reaches `payments.payment_records.status = SUCCEEDED` |
| `delivery_failure_loop` | `services/delivery/Chaos/ChaosAwareDeliverySimulator.cs` | `delivery.dlq` depth grows by N; `delivery.deliveries.status = COMPLETED` count for run orderIds stays 0 |
| `duplicate_events` | `services/order/Chaos/ChaosAwareEventPublisher.cs` | exactly one `payments.payment_records` row per orderId (idempotent consumer wins); payment-service logs `Skipping duplicate` ≥ N×(count-1) times |
| `kitchen_slowdown` | `services/kitchen/Chaos/ChaosAwareKitchenSimulator.cs` | `kitchen.kitchen_orders.prep_duration_ms ≥ 1200ms` for every run orderId; kitchen-service logs the chaos warning once per order |
| `broker_restart` | manual (DOG-48 harness) | invokes `scripts/chaos-broker-restart.sh`; passes when every order completes after the bounce |

---

## 2. Prerequisites

| Check | How |
|---|---|
| Docker stack up | `docker compose up -d` returns; `docker compose ps` shows every service `running` (and `healthy` where applicable) |
| Order API reachable | `curl -fsS http://localhost:5294/health` returns `{"status":"ok"}` |
| Dashboard API reachable | `curl -fsS http://localhost:5000/health` returns `{"status":"ok"}` |
| Clean baseline (recommended) | `docker compose down -v && docker compose up -d` if prior chaos runs left DLQ residue. The harness uses *deltas* on `delivery.dlq` so absolute residue is tolerable, but a clean run is easier to cite. |

---

## 3. Run the harness

```bash
bash scripts/chaos-smoke-tests.sh
```

Default parameters (override via env vars):

| Variable | Default | What it controls |
|---|---|---|
| `ORDERS_PER_SCENARIO` | `3` | How many orders each scenario drives. 3 is enough to prove "every order" without padding runtime |
| `DRAIN_TIMEOUT_SEC` | `30` | Phase-4 deadline for the synchronous-pipeline scenarios (delayed_payment, duplicate_events, kitchen_slowdown) |
| `DLQ_TIMEOUT_SEC` | `25` | Deadline for `delivery_failure_loop` to fill the DLQ (3 retries × 1+2+4s TTL + handler time) |
| `ORDER_API` | `http://localhost:5294` | Where order POSTs go |
| `DASHBOARD_API` | `http://localhost:5000` | Where chaos toggles go |

A timestamped run log is written to `docs/test/evidence/dog-50/run-<UTC>.log`. The nested `broker-restart/` subdirectory captures DOG-48's own harness log for the same run.

Exit codes: `0` = ALL PASS, `1` = pre-flight error, `2` = one or more scenarios FAIL.

---

## 4. What the harness does, scenario by scenario

For every scenario it follows the same outer loop:

1. **Reset** — flip every chaos row to `enabled = false` (`POST /chaos/set` to dashboard-api).
2. **Arm** — flip the target scenario on with its parameters.
3. **Drive** — POST `ORDERS_PER_SCENARIO` orders against `order-service`.
4. **Observe** — poll the scenario-specific oracle (DB / RabbitMQ / log).
5. **Disarm** — flip the scenario back off.
6. **Record** — append a `VERDICT [<scenario>]: PASS | FAIL <reason>` line to the run log.

After all four toggleable scenarios run, the harness invokes `scripts/chaos-broker-restart.sh` (DOG-48) as scenario 5. Its run log lands under `docs/test/evidence/dog-50/broker-restart/`. A final Summary section prints one line per scenario and exits non-zero if any FAILED.

### 4.1 `delayed_payment` (Scenario 1/5)

Sets `{"delay_ms": 2000}`, POSTs 3 orders, waits until 3 `payments.payment_records` rows reach `status = SUCCEEDED`. Then greps `docker logs reservoir-payment --since 60s` for `Chaos [delayed_payment] active` and asserts ≥ 3 matches. PASS when both conditions hold — every order paid, every payment passed through the chaos decorator.

### 4.2 `delivery_failure_loop` (Scenario 2/5)

Captures the current `delivery.dlq` depth via the RabbitMQ management API, enables the scenario, POSTs 3 orders, polls until the depth has grown by ≥ 3, then asserts that none of the run's orderIds appears in `delivery.deliveries` with `status = COMPLETED`. The DLQ delta (not the absolute depth) is the canonical oracle so residue from prior chaos runs is tolerable.

### 4.3 `duplicate_events` (Scenario 3/5)

Sets `{"count": 3}`, POSTs 3 orders. Each `order.created` is therefore published 3 times. After waiting for 3 `SUCCEEDED` payments, asserts:

- `COUNT(DISTINCT order_id)` = `COUNT(*)` = 3 in `payments.payment_records` for the run orderIds (idempotent consumer prevented double-writes).
- payment-service logged `Skipping duplicate` at least `3 × (3 − 1) = 6` times in the last 90s (the 2 redundant publishes per order were each detected as duplicates).

### 4.4 `kitchen_slowdown` (Scenario 4/5)

Sets `{"factor": 5}` (instead of the default 10 — keeps runtime short while still being unambiguously distinguishable from baseline). POSTs 3 orders, waits for 3 `kitchen_orders` rows to reach `READY`, then asserts:

- `prep_duration_ms ≥ 1200ms` for every row (factor 5 × baseline ~300-600ms ⇒ 1500-3000ms; 1200ms is the generous lower bound).
- kitchen-service logged the `Chaos [kitchen_slowdown] active` warning at least 3 times.

The average `prep_duration_ms` is logged so the operator can eyeball the multiplier.

### 4.5 `broker_restart` (Scenario 5/5)

Delegates to `scripts/chaos-broker-restart.sh` (DOG-48), passing `ORDER_COUNT=$ORDERS_PER_SCENARIO` and redirecting its evidence into `docs/test/evidence/dog-50/broker-restart/`. PASS when the DOG-48 harness exits 0.

---

## 5. Reference run (2026-06-06)

Executed on `feature/DOG-50-chaos-scenarios-smoke-test` against the default 4-service compose stack:

```
[16:29:54Z] === DOG-50 chaos smoke tests ===
[16:29:54Z] ORDERS_PER_SCENARIO=3  RUN_TAG=20260606T162954Z
[16:29:54Z] === Scenario 1/5 — delayed_payment ===
[16:30:03Z]   succeeded payments = 3/3
[16:30:03Z]   payment-service: '3' delayed_payment chaos warnings in last 60s
[16:30:03Z] VERDICT [delayed_payment]: PASS
[16:30:03Z] === Scenario 2/5 — delivery_failure_loop ===
[16:30:15Z]   delivery.dlq after: 7  (delta=3)
[16:30:16Z]   delivery.deliveries COMPLETED for run orderIds: 0 (expected 0)
[16:30:16Z] VERDICT [delivery_failure_loop]: PASS
[16:30:16Z] === Scenario 3/5 — duplicate_events ===
[16:30:17Z]   succeeded payments = 3/3
[16:30:19Z]   distinct orderIds with payment: 3 / 3
[16:30:19Z]   total payment_records (should equal distinct): 3
[16:30:19Z]   payment-service 'Skipping duplicate' log lines in last 90s: 6 (expected >= 6)
[16:30:19Z] VERDICT [duplicate_events]: PASS
[16:30:19Z] === Scenario 4/5 — kitchen_slowdown ===
[16:30:26Z]   ready kitchen_orders = 3/3
[16:30:27Z]   kitchen_orders with prep_duration_ms>=1200: 3 / 3
[16:30:27Z]   avg prep_duration_ms: 2057
[16:30:27Z]   kitchen-service chaos warnings in last 60s: 3
[16:30:28Z] VERDICT [kitchen_slowdown]: PASS
[16:30:28Z] === Scenario 5/5 — broker_restart (delegates to DOG-48 harness) ===
[16:30:45Z] VERDICT [broker_restart]: PASS
[16:30:45Z] === Summary ===
[16:30:45Z]   delayed_payment: PASS
[16:30:45Z]   delivery_failure_loop: PASS
[16:30:45Z]   duplicate_events: PASS
[16:30:45Z]   kitchen_slowdown: PASS
[16:30:45Z]   broker_restart: PASS
[16:30:45Z] ALL PASS — 5 / 5 scenarios green.
```

End-to-end wall clock: **51 s** for all 5 scenarios with `ORDERS_PER_SCENARIO=3`.

Full log lives at `docs/test/evidence/dog-50/run-<UTC>.log` and the nested DOG-48 sub-log at `docs/test/evidence/dog-50/broker-restart/run-<UTC>.log`. The `docs/test/evidence/` tree is gitignored (`*.log` rule) by repo convention — copy the run files into the thesis evidence pack rather than committing them.

---

## 6. Visual verification via the DOG-49 panel

The harness exercises chaos via `POST /chaos/set`. To cover the panel side as well, run a single scenario manually:

1. Open `http://localhost:3000` (dashboard).
2. Scroll to the **Chaos panel**.
3. Toggle, e.g., **Kitchen slowdown** to ON, leave `factor = 10`.
4. Confirm a red "Chaos active" banner appears across the top of the panel.
5. POST one order against `http://localhost:5294/orders` (or via curl from the docs).
6. In another terminal: `docker logs -f reservoir-kitchen | grep Chaos` — expect the warning within seconds.
7. Toggle the scenario back off — within ≤ 5 s (the panel's poll interval) the banner clears.

Screenshot the panel with the banner visible and save under `docs/test/evidence/dog-50/` for the thesis evidence pack.

---

## 7. Failure modes worth knowing

| Symptom | Likely cause |
|---|---|
| Scenario 1/3 FAILs with "payments did not complete in time" | Payment service not consuming. Check `docker logs reservoir-payment` for AMQP connection errors. |
| Scenario 2 FAILs with `delta=0` | `delivery_failure_loop` chaos row never reached the delivery container — check `chaos.chaos_config` directly with psql. |
| Scenario 3 FAILs with `dup_logs < expected` | `duplicate_events` row landed but `count` parsed back to 1 (treat-as-no-op). Re-check the JSON body sent. |
| Scenario 4 FAILs with `long_preps=0` and `avg_ms≈400` | Kitchen container hadn't picked up the chaos row yet — usually `kitchen-service` container was restarted very recently. Re-run. |
| Scenario 5 FAILs | Read the nested `broker-restart/run-*.log` — failure modes are documented in `docs/test/dog-48-broker-restart-survival.md` §6. |

---

## 8. Evidence checklist (for thesis Results chapter)

- [x] `docs/test/evidence/dog-50/run-<UTC>.log` — captured automatically by the harness
- [x] `docs/test/evidence/dog-50/broker-restart/run-<UTC>.log` — DOG-48 nested log
- [ ] Screenshot of the DOG-49 panel with a scenario toggled ON (red banner visible)
- [ ] Optional: screenshot of the RabbitMQ Queues tab showing `delivery.dlq` after scenario 2

---

## 9. Findings (canonical thesis run)

- **Test executed on:** 2026-06-06 16:29:54Z
- **Tester:** Otar Abashidze
- **Commit at test time:** see `git rev-parse HEAD` on `feature/DOG-50-chaos-scenarios-smoke-test`
- **`ORDERS_PER_SCENARIO`:** 3
- **End-to-end wall clock:** 51 s
- **Per-scenario verdicts:**
  - `delayed_payment`: PASS (3/3 succeeded, 3 chaos warnings observed)
  - `delivery_failure_loop`: PASS (delta=3 on `delivery.dlq`, 0 completed deliveries for run orderIds)
  - `duplicate_events`: PASS (1 payment per order, 6 "Skipping duplicate" log lines)
  - `kitchen_slowdown`: PASS (avg `prep_duration_ms` = 2057 vs baseline ~400 — ~5× multiplier confirmed)
  - `broker_restart`: PASS (broker healthy after 14 s, all 3 orders COMPLETED post-bounce)
- **DOG-50 acceptance status:** PASS

---

## Related documents

- DOG-42 — DLQ + retry verification (per-service DLX/DLQ foundation)
- DOG-48 — broker restart survival (chaos scenario 5 lives there)
- ARCH-005 — chaos scenarios architecture (`docs/arch/ARCH-005-chaos-scenarios.md`)
