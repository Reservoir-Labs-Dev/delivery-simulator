# Experiment 1 — Happy-path baseline (50 orders, no chaos)

**DOG-53** · Milestone M4 — Experiments & Metrics · scenario `none`

This is the **control** for every other M4 experiment. All chaos toggles are
disabled, so the numbers here represent the pipeline's behaviour under normal
operation. Subsequent experiments (DOG-54..DOG-57) are interpreted as deltas
against this baseline.

## Setup

| Parameter | Value |
| --- | --- |
| Orders posted | 50 |
| Scenario | `none` (all chaos disabled) |
| Posting rate | unbounded (burst — all 50 POSTed in ~11s) |
| Run tag | `exp1-baseline-20260610T195051Z` |
| Run window | `2026-06-10T19:50:54.875599Z` → `2026-06-10T19:51:37.250080Z` |
| Metric rows captured | 150 (50 orders × 3 metric-writing stages) |

Reproduce with:

```bash
scripts/run-experiment.sh --experiment-id exp1-baseline --orders 50 --scenario none
```

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
| payment | 50 | 254 | 253 | 374 |
| kitchen | 50 | 409 | 440 | 515 |
| delivery | 50 | 582 | 529 | 720 |
| **sum (avg)** | | **≈ 1245** | | |

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
| avg | 11 453 |
| p50 | 11 347 |
| p95 | 17 352 |
| min | 4 637 |
| max | 18 049 |

**Why is this ~9× the sum of per-stage processing (~1.2s)?** All 50 orders were
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
> `docs/experiments/runs/exp1-baseline-20260610T195051Z/` (`metrics.csv`,
> `summary.txt`, `run.log`). That folder is gitignored as transient working
> output; the CSV above is the committed copy of record.

## Takeaways for the control

- **Correctness:** 100% success across all three stages, zero retries, zero DLQ.
  Any failure or retry in a chaos experiment is therefore attributable to the
  injected fault, not to baseline flakiness.
- **Per-stage processing baseline:** payment ≈ 254ms, kitchen ≈ 409ms,
  delivery ≈ 582ms (avg). These are the reference values other experiments are
  compared against.
- **Load note:** under an unbounded 50-order burst the pipeline does not drop or
  fail anything; it simply queues. End-to-end latency reflects that queueing,
  not per-stage slowness.
