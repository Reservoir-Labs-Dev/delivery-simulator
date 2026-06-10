# Experiment 1 — Happy-path baseline (50 orders, no chaos)

**DOG-53** · Milestone M4 — Experiments & Metrics · scenario `none`

This is the **control** for every other M4 experiment. All chaos toggles are
disabled, so the numbers here represent the pipeline's behaviour under normal
operation. Subsequent experiments (DOG-54..DOG-57) are interpreted as deltas
against this baseline.

## Method

Measured with `scripts/run-experiment.sh`, which applies the standard protocol
to **every** experiment (so baseline and chaos runs are comparable):

1. **Warmup (discarded):** 20 orders are posted with chaos off and allowed to
   drain, to warm .NET JIT, EF Core query compilation, and connection pools.
   These rows start before `run_start_utc`, so they fall outside the
   measurement window and are excluded automatically. This controls the
   cold-service confound noted in
   [`../research/experimental-design-summary.md`](../research/experimental-design-summary.md)
   (internal validity).
2. **Cooldown:** a short settle so warmup work fully clears the queues.
3. **Measured batch:** the 50 orders reported below.

Reproduce with:

```bash
scripts/run-experiment.sh --experiment-id exp1-baseline --orders 50 --scenario none
```

| Parameter | Value |
| --- | --- |
| Warmup orders (discarded) | 20 |
| Measured orders | 50 |
| Scenario | `none` (all chaos disabled) |
| Posting rate | unbounded (burst — all 50 POSTed in ~11s) |
| Run tag | `exp1-baseline-20260610T204958Z` |
| Run window | `2026-06-10T20:50:38.510744Z` → `2026-06-10T20:51:17.874161Z` |
| Metric rows captured | 150 (50 orders × 3 metric-writing stages) |

> The order-service does not write to `metrics.metrics`, so the three
> metric-writing stages are **payment → kitchen → delivery**. 50 orders × 3
> stages = 150 rows.

## Outcomes

Every order completed every stage successfully. No retries, no failures, no
DLQ-bound messages — exactly what a healthy control should show.

| Stage | Count | SUCCESS | FAILED | Max retry_count | DLQ-bound |
| --- | --- | --- | --- | --- | --- |
| payment | 50 | 50 | 0 | 0 | 0 |
| kitchen | 50 | 50 | 0 | 0 | 0 |
| delivery | 50 | 50 | 0 | 0 | 0 |

## Per-stage processing time

Time spent **inside** each stage (`completed_at − started_at`), i.e. the actual
work — excluding any time the message waited in a queue. Quantiles are
interpolated (`percentile_cont`) in Postgres.

| Stage | n | avg (ms) | p50 (ms) | p95 (ms) |
| --- | --- | --- | --- | --- |
| payment | 50 | 242 | 238 | 335 |
| kitchen | 50 | 373 | 368 | 513 |
| delivery | 50 | 523 | 513 | 696 |
| **sum (avg)** | | **≈ 1138** | | |

Delivery is the slowest stage, payment the fastest. Even at p95 the stages stay
well-bounded, so per-stage processing is stable and there are no slow outliers
under normal load.

## End-to-end latency (wall-clock per order)

Span from the first stage starting to the last stage completing for a given
order (`MAX(completed_at) − MIN(started_at)` grouped by `order_id`). Unlike the
per-stage table above, this **includes inter-stage queue wait**.

| Metric | Value (ms) |
| --- | --- |
| orders | 50 |
| avg | 7 959 |
| p50 | 7 979 |
| p95 | 13 859 |
| min | 956 |
| max | 14 654 |

**Why is this ~7× the sum of per-stage processing (~1.1s)?** All 50 orders were
POSTed in an 11-second burst with no pacing, so they arrived faster than the
pipeline drained them. Most of each order's wall-clock time is therefore *queue
wait*, not work. This is expected for an unbounded burst and is a property of
the load profile, not a fault — the per-stage processing table is the right
measure of the pipeline's intrinsic speed. Experiments that vary load can use
`--rate` to control this.

## Raw data

- Raw metrics CSV (committed): [`exp1-baseline.csv`](exp1-baseline.csv) — 150 rows.

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

> The runner also writes a full per-run folder at
> `docs/experiments/runs/exp1-baseline-20260610T204958Z/` (`metrics.csv`,
> `summary.txt`, `run.log`). That folder is gitignored as transient working
> output; the CSV above is the committed copy of record.

## Threats to validity

Per [`../research/experimental-design-summary.md`](../research/experimental-design-summary.md):

- **Conclusion validity (small N):** this is a single run of 50 orders. As the
  methodology states, we report descriptive statistics (avg / p50 / p95 / min /
  max) rather than significance tests — appropriate for an exploratory study at
  this scale. Absolute numbers will drift run-to-run; the chaos experiments are
  read as *relative* deltas against this control, all measured with the same
  warmup + cooldown protocol.
- **Internal validity (cold start):** mitigated by the warmup batch (see
  *Method*). An earlier un-warmed run measured payment/delivery seconds after a
  restart and reported ~5–10% higher latencies; the warm run above is the one of
  record.
- **Internal validity (host load):** experiments are run sequentially on the
  same machine with other applications closed, with a cooldown between runs.

## Takeaways for the control

- **Correctness:** 100% success across all three stages, zero retries, zero DLQ.
  Any failure or retry in a chaos experiment is therefore attributable to the
  injected fault, not to baseline flakiness.
- **Per-stage processing baseline:** payment ≈ 242ms, kitchen ≈ 373ms,
  delivery ≈ 523ms (avg). These are the reference values other experiments are
  compared against.
- **Load note:** under an unbounded 50-order burst the pipeline does not drop or
  fail anything; it simply queues. End-to-end latency reflects that queueing,
  not per-stage slowness.
