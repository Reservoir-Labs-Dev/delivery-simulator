# Experiment 2 — Payment delay (50 orders, `delayed_payment` @ 5000 ms)

**DOG-54** · Milestone M4 — Experiments & Metrics · scenario `delayed_payment`

Injects a slow external payment gateway: every `order.created` is held for
`delay_ms` before the payment simulator runs (implemented by
[`ChaosAwarePaymentSimulator`](../../services/payment/Chaos/ChaosAwarePaymentSimulator.cs)).
This is a **pure latency fault** — payment still succeeds, it just takes longer.
Results are read as deltas against the
[Experiment 1 baseline](exp1-baseline.md) (scenario `none`), measured with the
identical warmup + cooldown protocol so the two are comparable.

## Method

Same standard protocol as the baseline (see
[`run-experiment.sh`](../../scripts/run-experiment.sh)): 20 discarded warmup
orders with chaos off → cooldown → enable `delayed_payment` → measured batch of
50 orders → drain → snapshot the metric rows in the run window.

Reproduce with:

```bash
scripts/run-experiment.sh \
  --experiment-id exp2-payment-delay \
  --orders 50 \
  --scenario delayed_payment \
  --params '{"delay_ms":5000}'
```

| Parameter | Value |
| --- | --- |
| Warmup orders (discarded) | 20 |
| Measured orders | 50 |
| Scenario | `delayed_payment`, `params.delay_ms = 5000` |
| Posting rate | unbounded (burst — all 50 POSTed in ~6s) |
| Run tag | `exp2-payment-delay-20260610T210550Z` |
| Run window | `2026-06-10T21:06:18.303097Z` → `2026-06-10T21:10:52.280397Z` |
| Metric rows captured | 150 (50 orders × 3 metric-writing stages) |

## Outcomes

Every order completed every stage successfully — **the latency fault does not
trigger the retry or DLQ path**. A 5 s gateway delay degrades *speed*, not
*correctness*: payment never throws, so there is nothing for the retry/DLQ
machinery to act on.

| Stage | Count | SUCCESS | FAILED | Max retry_count | DLQ-bound |
| --- | --- | --- | --- | --- | --- |
| payment | 50 | 50 | 0 | 0 | 0 |
| kitchen | 50 | 50 | 0 | 0 | 0 |
| delivery | 50 | 50 | 0 | 0 | 0 |

**Retry count per order: 0.0 (avg). Recovery rate: 100% (50/50), same as
baseline** — no order needed recovery because none failed.

## Per-stage processing time

Time spent **inside** each stage (`completed_at − started_at`), versus the
baseline. The fault is confined to the payment stage.

| Stage | n | avg (ms) | p50 (ms) | p95 (ms) | baseline avg | Δ avg |
| --- | --- | --- | --- | --- | --- | --- |
| payment | 50 | 5 225 | 5 223 | 5 319 | 242 | **+4 983** |
| kitchen | 50 | 367 | 369 | 504 | 373 | −6 |
| delivery | 50 | 530 | 532 | 684 | 523 | +7 |

Payment's `+4 983 ms` is, to within noise, exactly the injected 5 000 ms delay
on top of its intrinsic ~242 ms of work — confirming the fault does what it
says and nothing more. Kitchen and delivery are unchanged (within run-to-run
noise), confirming the fault is **isolated to payment** and does not propagate
as slower processing downstream.

## End-to-end latency (wall-clock per order)

Span from an order's first stage starting to its last stage completing
(`MAX(completed_at) − MIN(started_at)` per `order_id`), including inter-stage
queue wait.

| Metric | Exp 2 (ms) | Baseline (ms) | Δ |
| --- | --- | --- | --- |
| orders | 50 | 50 | |
| avg | 6 105 | 7 959 | **−1 854** |
| p50 | 6 111 | 7 979 | −1 868 |
| p95 | 6 360 | 13 859 | **−7 499** |
| min | 5 777 | 956 | +4 821 |
| max | 6 545 | 14 654 | −8 109 |

**The counter-intuitive result:** despite adding a 5 s delay to every order,
mean end-to-end latency *dropped* (−1.9 s) and the distribution became far
tighter (p95 6.4 s vs 13.9 s; spread of ~0.8 s vs ~13.7 s).

Why: in the baseline, all 50 orders burst into the pipeline at once and pile up
in the kitchen/delivery queues, so most of each order's wall-clock time is
*queue wait*, with a long tail (max 14.7 s). The 5 s payment delay acts as an
**unintentional rate limiter** — it spreads the orders out in time, so by the
time each reaches kitchen and delivery the downstream queues are nearly empty.
Each order's wall-clock is then dominated by the fixed 5 s delay plus minimal
queueing (min 5.78 s ≈ 5 s delay + the three stages' processing with no
contention), which is why the distribution is so tight.

This is a load-shape effect, not evidence that the delay is "good": it is an
artefact of the unbounded burst profile. The honest reading is that **under a
bursty load, the dominant cost in the baseline is queue contention, not
per-stage work** — a 5 s serialising delay trades a higher floor for a much
lower and more predictable ceiling.

## Raw data

- Raw metrics CSV (committed): [`exp2-payment-delay.csv`](exp2-payment-delay.csv) — 150 rows.

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

> The runner also writes a full per-run folder at
> `docs/experiments/runs/exp2-payment-delay-20260610T210550Z/` (`metrics.csv`,
> `summary.txt`, `run.log`), gitignored as transient output; the CSV above is
> the committed copy of record.

## Threats to validity

Per [`../research/experimental-design-summary.md`](../research/experimental-design-summary.md):

- **Conclusion validity (small N):** single run of 50 orders; descriptive
  statistics only, read as relative deltas against the baseline (same protocol).
- **Construct validity (what "delay" models):** `delayed_payment` models a slow
  but *working* gateway. It deliberately does not model a gateway *failure*
  (timeout/error) — that path is exercised by the failure-injection scenarios.
  So the "0 retries / 100% recovery" result is a property of this fault type,
  not of the system's resilience to payment errors.
- **Internal validity (load shape):** the headline end-to-end improvement is an
  artefact of the unbounded burst interacting with the delay (see above). A
  paced run (`--rate`) would isolate the per-order delay cost from queueing and
  is the natural follow-up if we want to separate the two.
- **Internal validity (cold start / host load):** mitigated by warmup + cooldown
  and sequential runs on the same idle host, identical to the baseline.

## Takeaways

- **Correctness is unaffected by latency:** a 5 s payment delay produces 100%
  success, 0 retries, 0 DLQ. Latency faults and failure faults are distinct;
  only the latter exercise retry/DLQ.
- **The fault is well-isolated:** payment absorbs the full delay
  (+≈5 s); kitchen and delivery are unchanged. No latency amplification
  downstream.
- **Queueing dominates under burst load:** the delay's serialising side-effect
  *reduced* tail end-to-end latency, revealing that the baseline's wall-clock
  cost is mostly queue contention, not per-stage processing.
