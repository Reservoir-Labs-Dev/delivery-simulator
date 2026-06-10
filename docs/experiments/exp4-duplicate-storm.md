# Experiment 4 — Duplicate storm (50 orders, each published 3×)

**DOG-56** · Milestone M4 — Experiments & Metrics · scenario `duplicate_events`

With `duplicate_events` enabled (`count = 3`), the order-service publishes every
`order.created` event **three times with the same `messageId`/`EventId`**
(implemented by
[`ChaosAwareEventPublisher`](../../services/order/Chaos/ChaosAwareEventPublisher.cs)),
modelling the at-least-once redelivery a broker produces under retries,
reconnects, or a crash between "work done" and "ack". Read as a delta against the
[Experiment 1 baseline](exp1-baseline.md), same warmup + cooldown protocol.

> **What this experiment is actually testing.** A real broker guarantees
> *at-least-once* delivery, so the same message can legitimately arrive more than
> once. The fault-tolerance property under test is the **idempotent consumer**
> pattern: at-least-once delivery + a dedup guard must yield *effectively-once*
> side effects. The interesting numbers are therefore not "duplicates were sent"
> (that is the injected fault) but **how many real side effects occurred** (must
> equal the order count, never more) and **whether a duplicate ever escaped
> downstream**. This is the "no double-processing" property; it complements the
> contain/bound/preserve story of [Experiment 3](exp3-delivery-dlq.md).

## How the guard works

Each consumer keeps a `processed_event_ids` ledger keyed by the inbound
`EventId`. In
[`OrderCreatedHandler`](../../services/payment/Handlers/OrderCreatedHandler.cs)
the payment service, in **one transaction**, writes the `payment_records` row and
inserts the `EventId` into `processed_event_ids`. A redelivered copy finds the id
already present, writes a `SKIPPED_DUPLICATE` metric row, and acks without
re-charging. Because the dedup insert and the business write share a transaction,
even two copies racing concurrently can only commit once — the second loses on the
`event_id` primary key, fails, and on its redelivery sees the id and skips.

## Method

Standard runner protocol (see [`run-experiment.sh`](../../scripts/run-experiment.sh)):
20 discarded warmup orders with chaos off → cooldown → enable `duplicate_events`
with `count = 3` → 50 measured orders → drain.

Reproduce with:

```bash
scripts/run-experiment.sh \
  --experiment-id exp4-duplicate-storm \
  --orders 50 \
  --scenario duplicate_events \
  --params '{"count":3}'
```

| Parameter | Value |
| --- | --- |
| Measured orders | 50 |
| Scenario | `duplicate_events` (`order.created` published 3× with same `EventId`) |
| Run tag | `exp4-duplicate-storm-20260610T221430Z` |
| Run window | `2026-06-10T22:14:57.637181Z` → `2026-06-10T22:15:35.978243Z` |
| Metric rows captured | 250 |

> 50 orders × 3 copies = **150 `order.created`** delivered to payment, of which
> 50 are processed and 100 skipped (150 payment rows). Payment emits exactly one
> `payment.succeeded` per order, so kitchen and delivery see 50 each → 150 + 50 +
> 50 = 250 rows.

## Result 1 — The storm is real (the fault arrived)

The duplicates were genuinely delivered and reached the consumer — this is not a
case of the publisher quietly collapsing them:

| Stage | Events received | Interpretation |
| --- | --- | --- |
| payment | 150 | 50 orders × 3 copies — the full storm |
| kitchen | 50 | one `payment.succeeded` per order |
| delivery | 50 | one `order.ready` per order |

Payment is the stage exposed to the storm (it consumes `order.created`); the 150
inbound events confirm every order was tripled as configured.

## Result 2 — Effectively-once side effects (no double-processing)

Despite 150 inbound payment events, exactly **50 charges** were applied — one per
order, zero double charges. The dedup guard absorbed all 100 redeliveries:

| payment outcome | metric rows | meaning |
| --- | --- | --- |
| `SUCCESS` | 50 | first copy of each order — charged once |
| `SKIPPED_DUPLICATE` | 100 | the 2nd and 3rd copies — recognised and skipped |

Every order followed the identical pattern — **1 SUCCESS + 2 SKIPPED_DUPLICATE,
for all 50 orders** (no order charged twice, none skipped on its first sighting):

| SUCCESS per order | SKIPPED per order | # orders |
| --- | --- | --- |
| 1 | 2 | 50 |

Cross-checked directly against the payment service's own tables (not just the
metrics stream), the side effects are exactly-once:

| Check (in run window) | Value | Meaning |
| --- | --- | --- |
| `payments.payment_records` rows | 50 | one charge per order |
| distinct `order_id` in those rows | 50 | no order charged twice |
| `order_id` with >1 payment record | **0** | **zero double-side-effects** |
| `payments.processed_event_ids` rows | 50 | the idempotency ledger — one entry per unique event |
| distinct `event_id` in the ledger | 50 | — |

The metrics-derived count (50 SUCCESS) and the authoritative business table (50
`payment_records`, 0 with a duplicate) agree exactly. **This is the result:** an
at-least-once stream of 150 messages produced exactly 50 effects.

## Result 3 — Duplicates do not propagate downstream

The guard skips a duplicate *before* it does any work or publishes anything, so
the storm is contained at the first stage. Payment publishes `payment.succeeded`
only on the SUCCESS path (once per order), so kitchen and delivery never see the
amplification — each processed exactly 50 events at baseline latency:

| Stage | Count | Outcome | avg (ms) | baseline avg | Δ |
| --- | --- | --- | --- | --- | --- |
| kitchen | 50 | 50 SUCCESS | 385 | 373 | +12 |
| delivery | 50 | 50 SUCCESS | 521 | 523 | −2 |

A skip is also cheap: skipped payment rows are a single indexed `event_id`
look-up, pulling payment's p50 down to **3 ms** (vs ~242 ms baseline for real
work) even as its average sits at 80 ms over the mixed 150-row population.

## Result 4 — Nothing lost, nothing dead-lettered

The storm caused **zero failures and zero retries** this run: 0 `FAILED` payment
rows, `retry_count = 0` across all 150 payment events, and `payment.dlq` empty.
No copy of any message was lost or dead-lettered — duplicates were resolved
inline by the guard, not by the failure/retry machinery.

> The live `delivery.dlq` (50) and `kitchen.dlq` (1) seen at capture time are
> **durable left-overs from Experiment 3** (DOG-55's total delivery outage), not
> products of this run. The relevant proof for *this* experiment is
> `payment.dlq = 0` together with zero `FAILED`/retry rows in the window — the
> duplicate storm dead-lettered nothing.

## Raw data

- Raw metrics CSV (committed): [`exp4-duplicate-storm.csv`](exp4-duplicate-storm.csv) — 250 rows.

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

## Threats to validity

- **Construct validity (what this tests):** a duplicate storm exercises the
  *idempotent-consumer* property — exactly-once effects under at-least-once
  delivery. It does not test ordering or concurrent-race dedup beyond what the
  broker's prefetch produced here (no PK-collision retries fired this run, so the
  transactional race path was not forced). The guard's correctness under a forced
  race is covered by the service unit/integration tests, not this run.
- **Conclusion validity (small N):** single 50-order run; descriptive stats, read
  as deltas against the baseline under the same protocol.
- **Internal validity (cold start / host load):** mitigated by warmup + cooldown
  and sequential runs on an idle host, identical to the baseline.

## Takeaways

- **Effectively-once:** 150 at-least-once deliveries → exactly 50 charges, 50
  distinct orders, **0 double-charges** (confirmed against `payment_records`, not
  just metrics).
- **Cheap suppression:** 100 duplicates recognised via the `processed_event_ids`
  ledger and skipped at p50 = 3 ms, never re-running business logic.
- **Containment:** because the guard skips before publishing, duplicates never
  propagate — kitchen and delivery saw exactly 50 events at baseline latency.
- **No loss:** 0 failures, 0 retries, empty `payment.dlq` — the storm was handled
  inline, not by the DLQ.
