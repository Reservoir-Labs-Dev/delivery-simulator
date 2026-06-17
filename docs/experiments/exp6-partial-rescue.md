# Experiment 6 — Partial delivery failure (50 orders, retry rescue)

**DOG-133** · Milestone M4 — Experiments & Metrics · scenario `delivery_failure_loop` with `fail_probability` < 1.0

Where [Experiment 3](exp3-delivery-dlq.md) drove a *total* delivery outage
(`fail_probability = 1.0`, every attempt fails → every order dead-lettered),
this experiment makes each delivery attempt fail *independently* with
probability `p < 1.0`. Because every redelivery re-enters
[`ChaosAwareDeliverySimulator`](../../services/delivery/Chaos/ChaosAwareDeliverySimulator.cs)
and rolls afresh, an order that fails an early attempt can still **succeed on a
retry**. That is the property under test: the retry ladder (DOG-38) is not just
a delay before the DLQ — it actively *rescues* orders hit by a transient fault.

> **What this experiment is actually testing.** Exp 3 showed the system
> *contains* and *preserves* a fault. But a 100% fault is the easy case to reason
> about — nothing is ever rescued. Real provider faults are transient: a fraction
> of attempts fail. The question is whether the bounded retry budget converts
> transient failures into eventual successes, and how the **rescue rate** moves
> with the per-attempt failure rate. This is the "tolerate" result that a pure
> outage cannot show.

## Method

Standard runner protocol (see [`run-experiment.sh`](../../scripts/run-experiment.sh)):
20 discarded warmup orders (chaos off) → cooldown → enable
`delivery_failure_loop` with a `fail_probability` parameter → 50 measured orders
→ drain. `DRAIN_IDLE_SEC=8` so the 4s retry-queue TTL is never mistaken for an
idle pipeline. The run is repeated at `p` ∈ {0.25, 0.5, 0.75} to trace the rescue
curve; the [Experiment 1 baseline](exp1-baseline.md) (`p = 0`) and the
[Experiment 3 total outage](exp3-delivery-dlq.md) (`p = 1`) are the analytic
endpoints.

Reproduce one point with:

```bash
DRAIN_IDLE_SEC=8 scripts/run-experiment.sh \
  --experiment-id exp6-partial-rescue-p50 \
  --orders 50 \
  --scenario delivery_failure_loop \
  --params '{"fail_probability":0.5}'
```

| Parameter | Value |
| --- | --- |
| Measured orders (per point) | 50 |
| Scenario | `delivery_failure_loop` with `fail_probability` ∈ {0.25, 0.5, 0.75} |
| Per-attempt model | each of up to 4 attempts (initial + 3 retries) fails independently with probability `p` |
| Analytic expectation | order lost only if **all 4** attempts fail ⇒ P(lost) ≈ `p⁴`; P(delivered) ≈ `1 − p⁴` |
| Run tags | `exp6-partial-rescue-p25/-p50/-p75-20260617T06…Z` |

### How rescue is measured

The metrics table records one row per delivery *attempt* (`order_id`,
`retry_count`, `outcome`). Per order, the attempt sequence classifies it:

| Class | Signature in `metrics.metrics` (service = delivery) |
| --- | --- |
| **First-try success** | a single `SUCCESS` row at `retry_count = 0` |
| **Rescued by retry** | ≥1 `FAILED` row **and** a later `SUCCESS` row |
| **Lost to DLQ** | 4 `FAILED` rows, no `SUCCESS` |

```sql
WITH d AS (
  SELECT order_id,
         bool_or(outcome = 'SUCCESS') AS ok,
         bool_or(outcome = 'FAILED')  AS failed
  FROM metrics.metrics
  WHERE service_name = 'delivery'
    AND started_at >= '<run_start>'::timestamptz
    AND started_at <  '<run_end>'::timestamptz
  GROUP BY order_id)
SELECT count(*) FILTER (WHERE ok AND NOT failed) AS first_try_success,
       count(*) FILTER (WHERE ok AND failed)     AS rescued_by_retry,
       count(*) FILTER (WHERE NOT ok)            AS lost_to_dlq,
       count(*)                                  AS total_orders
FROM d;
```

The headline metric is the **retry-rescue rate** = `rescued_by_retry /
(rescued_by_retry + lost_to_dlq)` — of the orders that failed at least once, what
fraction did the retry budget save before the DLQ.

## Result 1 — Rescue curve (rescue rate vs per-attempt failure rate)

| `fail_probability` | first-try | rescued | lost to DLQ | failed ≥1× | delivered | **rescue rate**¹ | analytic P(lost)=p⁴ |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0.25 | 38 | 12 | 0  | 12 | **50/50 = 100%** | **12/12 = 100%** | 0.39% |
| 0.50 | 26 | 21 | 3  | 24 | **47/50 = 94%**  | **21/24 = 87.5%** | 6.25% |
| 0.75 | 13 | 28 | 9  | 37 | **41/50 = 82%**  | **28/37 = 75.7%** | 31.6% |
| 1.00 *(exp 3)* | 0 | 0 | 50 | 50 | 0/50 = 0% | 0/50 = 0% | 100% |

¹ rescue rate = rescued / (rescued + lost): of the orders that hit ≥1 failure, the share the retry ladder saved.

Two readings of the same data:

- **Retry is rescue, not just delay.** Even when three-quarters of every attempt
  fails (`p = 0.75`), the bounded retry budget still delivered **82%** of orders
  and rescued **76%** of those that stumbled. At `p = 0.5` it rescued 87.5%; at
  `p = 0.25` it rescued every order that failed (12/12) and **lost none**. The
  ladder converts transient failures into eventual successes.
- **The DLQ tail tracks `p⁴`.** Loss appears only when an order fails all four
  attempts. Measured loss (0 / 3 / 9 of 50) sits beside the analytic `p⁴`
  (0.2 / 3.1 / 15.8 expected). The `p = 0.25` and `p = 0.5` points match closely;
  `p = 0.75` came in below expectation (9 vs ~16) — see Threats: a single N = 50
  run carries Binomial sampling noise of roughly ±3 orders at this `p`.

## Result 2 — Isolation still holds under partial failure

At **every** `p`, the two upstream stages were untouched — payment and kitchen
each processed all 50 orders successfully at baseline latency, exactly as in the
total-outage case (exp 3):

| `fail_probability` | payment | kitchen |
| --- | --- | --- |
| 0.25 | 50 SUCCESS | 50 SUCCESS |
| 0.50 | 50 SUCCESS | 50 SUCCESS |
| 0.75 | 50 SUCCESS | 50 SUCCESS |

Partial failure does not widen the blast radius. Rescue is layered **on top of**
isolation, not traded against it — the fault stays inside delivery whether it
fires on 25% or 100% of attempts.

## Result 3 — Retry effort is paid only by failed attempts

The attempt-by-attempt breakdown (delivery rows, `succeeded / failed` at each
`retry_count`) shows the retry budget is consumed only by orders that actually
fail, and that each attempt fails at ≈ `p` — confirming the independent-roll
model:

**`p = 0.50`** (the cleanest demonstration — each attempt halves the survivors):

| attempt (`retry_count`) | orders reaching it | succeeded | failed → retry | empirical fail rate |
| --- | --- | --- | --- | --- |
| 0 | 50 | 26 | 24 | 0.48 |
| 1 | 24 | 12 | 12 | 0.50 |
| 2 | 12 | 6  | 6  | 0.50 |
| 3 | 6  | 3  | 3 → **DLQ** | 0.50 |

**`p = 0.25`** — fast decay, dry DLQ:

| attempt | reaching | succ | fail | 
| --- | --- | --- | --- |
| 0 | 50 | 38 | 12 |
| 1 | 12 | 7 | 5 |
| 2 | 5 | 2 | 3 |
| 3 | 3 | 3 | 0 → **DLQ** |

**`p = 0.75`** — heavy retry traffic, larger tail:

| attempt | reaching | succ | fail |
| --- | --- | --- | --- |
| 0 | 50 | 13 | 37 |
| 1 | 37 | 12 | 25 |
| 2 | 25 | 9 | 16 |
| 3 | 16 | 7 | 9 → **DLQ** |

First-try successes consume **zero** retry budget; the system only spends retries
on the orders that need them, and only the `p⁴` tail walks the full ladder into
the DLQ.

## Raw data

- Per-point metrics CSVs (committed): [`exp6-partial-rescue-p25.csv`](exp6-partial-rescue-p25.csv)
  (170 rows), [`exp6-partial-rescue-p50.csv`](exp6-partial-rescue-p50.csv)
  (192 rows), [`exp6-partial-rescue-p75.csv`](exp6-partial-rescue-p75.csv) (228 rows).

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

## Threats to validity

- **Construct validity (independent rolls).** Each attempt fails independently;
  a real provider fault may be correlated (a sustained outage looks like
  `p → 1`, a blip like `p → 0`). The sweep brackets both ends; the independence
  assumption is stated, and Result 3 confirms the empirical per-attempt rate ≈ `p`.
- **Conclusion validity (small N).** 50 orders per point: the lost count is a
  sample from Binomial(50, `p⁴`), so per-point figures carry sampling noise —
  most visible at `p = 0.75` (9 lost vs ~16 expected, ≈2σ low). Reported as
  descriptive counts against the analytic `p⁴`, not a fitted model; a larger N or
  repeated runs would tighten the tail. Not re-rolled for a closer fit.
- **Internal validity (cold start / host load).** Mitigated by the warmup +
  cooldown protocol, identical to the baseline and exp 3.

## Takeaways

- **Retry is rescue, not just delay.** Below a total outage the bounded retry
  budget converts most transient failures into eventual successes — rescue rate
  100% / 87.5% / 75.7% at `p` = 0.25 / 0.5 / 0.75, and overall delivery degrades
  gracefully (100% → 94% → 82%) rather than collapsing.
- **The DLQ tail follows `p⁴`.** Only orders that fail all four attempts are
  dead-lettered; loss shrinks fast as `p` drops and is ~0 by `p = 0.25`.
- **Containment is unchanged.** Payment and kitchen stay at 50/50 SUCCESS at
  every `p`. Rescue is added on top of isolation, not traded against it.
- This is the **rescue** third of the resilience story: contain + preserve
  (exp 3) → **rescue** (here) → recover ([exp 7](exp7-recovery.md)).
