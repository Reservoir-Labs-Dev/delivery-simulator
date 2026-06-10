# Experiment 3 — Delivery failure loop (50 orders, delivery fails 100%)

**DOG-55** · Milestone M4 — Experiments & Metrics · scenario `delivery_failure_loop`

Every delivery attempt throws before doing any work (implemented by
[`ChaosAwareDeliverySimulator`](../../services/delivery/Chaos/ChaosAwareDeliverySimulator.cs)),
modelling a total delivery-provider outage. Read as a delta against the
[Experiment 1 baseline](exp1-baseline.md), same warmup + cooldown protocol.

> **What this experiment is actually testing.** That a forced failure produces
> failures is trivial. The point is *what the rest of the system does when one
> service is completely down*: does the failure stay contained, is the retry
> effort bounded, and is any order lost? Those three properties — **isolation,
> bounded retry, zero loss** — are the fault-tolerance result, not the DLQ count
> itself. This is the "contain and preserve" half of the story; the "rescue" and
> "recover" halves are DOG-133 (partial failure) and DOG-134 (recovery).

## Method

Standard runner protocol (see [`run-experiment.sh`](../../scripts/run-experiment.sh)):
20 discarded warmup orders with chaos off → cooldown → enable
`delivery_failure_loop` → 50 measured orders → drain. The drain idle threshold
was raised to 8s (`DRAIN_IDLE_SEC=8`) so the 4s retry-queue TTL can't be
mistaken for an idle pipeline.

Reproduce with:

```bash
DRAIN_IDLE_SEC=8 scripts/run-experiment.sh \
  --experiment-id exp3-delivery-dlq \
  --orders 50 \
  --scenario delivery_failure_loop
```

| Parameter | Value |
| --- | --- |
| Measured orders | 50 |
| Scenario | `delivery_failure_loop` (delivery throws on every attempt) |
| Run tag | `exp3-delivery-dlq-20260610T214005Z` |
| Run window | `2026-06-10T21:40:38.258102Z` → `2026-06-10T21:41:19.877242Z` |
| Metric rows captured | 300 |

> 50 payment SUCCESS + 50 kitchen SUCCESS + 50 orders × **4 delivery attempts** =
> 300 rows. Delivery writes a `FAILED` metric row on each attempt (the handler's
> catch block), so a fully-retried order leaves 4 delivery rows.

## Result 1 — Failure isolation (blast radius)

Delivery was 100% down, yet the two upstream stages were **completely
unaffected** — same success rate and latency as the baseline:

| Stage | Count | Outcome | avg (ms) | baseline avg | Δ |
| --- | --- | --- | --- | --- | --- |
| payment | 50 | 50 SUCCESS | 206 | 242 | −36 |
| kitchen | 50 | 50 SUCCESS | 373 | 373 | 0 |
| delivery | 50 | **0 SUCCESS / 50 dead-lettered** | 5 | 523 | n/a (throws before work) |

The outage stayed inside delivery. In a synchronous or tightly-coupled design a
dead delivery step would block or fail the whole order flow; here payment and
kitchen neither slowed nor failed. **This isolation is the primary
fault-tolerance property** the experiment demonstrates.

## Result 2 — Bounded, predictable retry

Every order walked the full retry ladder exactly once and then stopped — no
infinite loop, no early give-up. The retry distribution is perfectly uniform:

| retry_count | delivery rows | meaning |
| --- | --- | --- |
| 0 | 50 | first attempt (all 50 orders) |
| 1 | 50 | after retry queue `.1` (1s backoff) |
| 2 | 50 | after retry queue `.2` (2s backoff) |
| 3 | 50 | after retry queue `.3` (4s backoff) → dead-lettered |

`MaxRetries = 3` ([`ConsumerRetry`](../../shared/Reservoir.BuildingBlocks/Messaging/ConsumerRetry.cs)),
so each order makes 4 attempts then dead-letters. The **time each order spent
being retried before dead-lettering** is tightly bounded — it equals the fixed
1+2+4s backoff, not a runaway:

| Time-to-DLQ (per order) | ms |
| --- | --- |
| avg | 7 055 |
| p50 | 7 044 |
| min | 7 034 |
| max | 7 246 |

A spread of ~200ms across 50 orders confirms the retry budget is deterministic
and bounded — the system spends a fixed, known amount of effort before deciding
a message is undeliverable.

## Result 3 — Zero data loss (the DLQ is the mechanism)

None of the 50 failed orders were dropped. All 50 landed intact in the
dead-letter queue, where they can be inspected and replayed once delivery
recovers (that recovery is DOG-134).

| Measure | Value | Cross-check |
| --- | --- | --- |
| Orders dead-lettered (metrics: FAILED, retry_count=3) | 50 | — |
| `delivery.dlq` depth (RabbitMQ) | 50 | independent ✓ |
| % orders ending in DLQ | 100% | matches ticket expectation |
| Orders lost / unaccounted | 0 | — |

The metric-derived count and the live RabbitMQ queue depth agree exactly,
corroborating that every failed order is preserved rather than lost.

## Raw data

- Raw metrics CSV (committed): [`exp3-delivery-dlq.csv`](exp3-delivery-dlq.csv) — 300 rows.

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

## Threats to validity

- **Construct validity (what 100% failure tests):** a total outage only
  exercises the *contain → bound → preserve* path; it cannot show *recovery*
  (nothing succeeds) or *rescue* (no transient successes). Those are deliberately
  split into DOG-133 (partial/transient failure — retry rescue rate) and
  DOG-134 (heal + drain DLQ — recovery rate / MTTR). This experiment is one
  third of the resilience story, not the whole of it.
- **Conclusion validity (small N):** single 50-order run; descriptive stats,
  read as deltas against the baseline under the same protocol.
- **Internal validity (cold start / host load):** mitigated by warmup + cooldown
  and sequential runs on an idle host, identical to the baseline.

## Takeaways

- **Isolation:** delivery fully down → payment and kitchen at 100% success,
  baseline latency. The fault did not propagate. (Blast radius = 1 service.)
- **Bounded retry:** exactly 4 attempts with 1/2/4s backoff, ~7s total, then
  stop — no infinite loop, no resource exhaustion, no premature drop.
- **Zero loss:** all 50 undeliverable orders preserved in the DLQ (confirmed
  against RabbitMQ), ready for replay — which is what makes recovery (DOG-134)
  possible.
