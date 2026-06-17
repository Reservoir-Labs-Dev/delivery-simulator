# Experiment 7 — Recovery after outage (heal + drain the DLQ)

**DOG-134** · Milestone M4 — Experiments & Metrics · `delivery_failure_loop` healed mid-run + `POST /admin/dlq/replay`

[Experiment 3](exp3-delivery-dlq.md) ended with 50 undeliverable orders
**preserved** in `delivery.dlq` — contained and not lost, but also not yet
delivered. This experiment closes that loop: it drives the same total outage,
then **heals the fault and replays the dead-letter queue** through the DOG-134
[`POST /admin/dlq/replay`](../../services/delivery/Program.cs) endpoint (backed by
the shared [`DlqReplayer`](../../shared/Reservoir.BuildingBlocks/Messaging/DlqReplayer.cs)),
and measures how completely and how quickly the preserved orders are recovered.

> **What this experiment is actually testing.** Preservation (exp 3) is only half
> the promise of a DLQ: the messages are safe, but the value is realised only if
> they can be **re-driven to success** once the fault clears. The two properties
> here are **recovery rate** (does every preserved order eventually complete — is
> the DLQ truly lossless end-to-end?) and **MTTR** (how long from initiating
> recovery to the last order delivered). Together with exp 3 this is the full
> *preserve → recover* path.

## Method

Driven by the DOG-134 recovery runner
([`run-recovery-experiment.sh`](../../scripts/run-recovery-experiment.sh)), which
extends the standard protocol with an explicit heal-and-replay phase:

1. **Clean slate** — disable all chaos, **purge `delivery.dlq`** so the count
   reflects only this run (the queue is durable and retains residue from prior
   runs — see exp 4's note).
2. **Warmup** — 20 orders, chaos off, drained (steady state).
3. **Outage** — enable `delivery_failure_loop` @ `fail_probability = 1.0`, post 50
   orders, wait until all 50 dead-letter (counted from the DB: a delivery
   `FAILED`/`retry_count = 3` row marks an order that exhausted its budget).
4. **Heal** — disable the scenario. The chaos config is read fresh from Postgres
   on every attempt ([`DbChaosConfigReader`](../../shared/Reservoir.BuildingBlocks/Persistence/DbChaosConfigReader.cs)),
   so the heal takes effect on the next delivery with no cache lag.
5. **Recover** — record `recovery_start`, then `POST /admin/dlq/replay`. The
   replayer drains the DLQ under publisher confirms and republishes each message
   to `orders.exchange`/`order.ready`.
6. **Measure** — wait until 50 delivery `SUCCESS` rows land post-heal; compute
   recovery rate and MTTR from the Postgres clock.

Reproduce with:

```bash
scripts/run-recovery-experiment.sh --experiment-id exp7-recovery --orders 50
```

| Parameter | Value |
| --- | --- |
| Measured orders | 50 |
| Outage | `delivery_failure_loop` @ `fail_probability = 1.0` (total) |
| Recovery trigger | `POST /admin/dlq/replay` on the delivery service (:5097) |
| Replay guarantees | zero-loss (publisher-confirm before DLQ ack), idempotent (original `MessageId` preserved, retry-count header dropped for a fresh budget) |
| Run tag | `exp7-recovery-20260617T065756Z` · window `06:58:27Z` → `06:59:24Z` · 350 metric rows |

### How recovery and MTTR are measured

- **dead-lettered** = delivery `FAILED`/`retry_count = 3` rows in the outage
  window, cross-checked against the live `delivery.dlq` depth.
- **replayed** = the count returned by the replay endpoint.
- **recovered** = delivery `SUCCESS` metric rows with `started_at ≥ recovery_start`.
- **recovery rate** = recovered / dead-lettered.
- **MTTR (DB clock)** = `max(completed_at) − recovery_start` over the recovered
  rows — single clock source, no polling jitter.

## Result 1 — Recovery rate (the DLQ is lossless end-to-end)

Every one of the 50 preserved orders was recovered. The three independent
counts — DB metrics, the replay endpoint's return value, and the live queue
depth — agree exactly:

| Measure | Value | Cross-check |
| --- | --- | --- |
| Dead-lettered (DB: `FAILED`, `retry_count = 3`) | 50 | every order walked the full 4-attempt ladder |
| Replayed (`POST /admin/dlq/replay` return) | 50 | drained from the live `delivery.dlq` |
| Recovered (delivery `SUCCESS` post-heal) | 50 | — |
| `delivery.dlq` depth after recovery | **0** | queue fully drained |
| **Recovery rate** | **100%** | — |
| Orders lost / unaccounted | **0** | — |

This is what makes the exp 3 result meaningful: preservation is not an end state
but a *holding pattern*. Once the fault is healed, replay re-drives every held
order to success — the 50 orders a synchronous, retry-less design would have
dropped are all delivered.

## Result 2 — MTTR (recovery is throughput-bound, not retry-bound)

| Measure | Value |
| --- | --- |
| MTTR (recovery_start → last order delivered, DB clock) | **27 181 ms (≈27.2 s)** |
| MTTR (wall clock, replay call → backlog drained) | 27.6 s |
| Recovered-order delivery latency — avg / p50 / p95 | **535 / 536 / 704 ms** |

The per-order delivery latency of the recovered orders (avg 535 ms) is
**indistinguishable from the [baseline](exp1-baseline.md) ≈523 ms** — each
replayed order is processed as ordinary healthy traffic, because replay grants a
fresh retry budget and the healed path succeeds on the first attempt. The ~27 s
MTTR is therefore **not** retry backoff (the 1+2+4 s ladder plays no part in
recovery); it is the time to push a 50-order backlog through the delivery
consumer at ~0.5 s/order. MTTR here is throughput-bound and scales roughly
linearly with backlog size — making consumer concurrency/prefetch the lever for
faster bulk recovery, a documented tuning point rather than a correctness issue.

## Result 3 — Idempotency holds across replay; isolation held during the outage

- **Exactly one delivery per order.** Replay preserves each message's original
  `MessageId`. Because the outage failed *before* any delivery row or
  `processed_event_id` was written, replay produced **50 `SUCCESS` rows for 50
  orders — no double-delivery** despite the preserved key.
- **Isolation held throughout.** Across the whole outage→recovery window, payment
  and kitchen were untouched — 50 `SUCCESS` each — while delivery logged 200
  `FAILED` (50 orders × 4 attempts during the outage) then 50 `SUCCESS` (post-heal):

| service | outcome | n |
| --- | --- | --- |
| payment | SUCCESS | 50 |
| kitchen | SUCCESS | 50 |
| delivery | FAILED | 200 |
| delivery | SUCCESS | 50 |

## Raw data

- Recovery-window metrics CSV (committed): [`exp7-recovery.csv`](exp7-recovery.csv) — 350 rows.

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

## Threats to validity

- **Construct validity (MTTR definition).** MTTR is measured from *initiating*
  recovery (the replay call), not from fault onset or an automated detector —
  replay here is operator-triggered. The figure is "time to drain and re-drive",
  a lower bound on real-world MTTR which would also include detection + decision
  time. Stated explicitly.
- **Internal validity (clean DLQ).** The DLQ is purged before the run so the
  measured count is attributable to this run only; the DB `FAILED/retry_count=3`
  signal (lag-free) drives completion, with the management-API depth as an
  independent cross-check.
- **Conclusion validity (small N).** Single 50-order run; descriptive figures.

## Takeaways

- **The DLQ is lossless end-to-end.** 50 preserved → 50 replayed → 50 recovered,
  DLQ drained to 0. Preservation (exp 3) plus replay (here) delivers 100% of the
  orders a retry-less design would have dropped.
- **Recovery is clean and bounded.** Recovered orders run at baseline latency on
  a fresh retry budget; MTTR is dominated by backlog throughput (~0.5 s/order),
  not by the retry ladder.
- **Replay is safe to re-run.** Idempotent (no double-delivery) and zero-loss
  (publisher-confirm before DLQ ack).
- This is the **recover** third of the resilience story: contain + preserve
  (exp 3) → rescue ([exp 6](exp6-partial-rescue.md)) → **recover** (here).
