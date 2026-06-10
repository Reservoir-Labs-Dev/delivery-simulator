# Experiment 5 — Kitchen slowdown (50 orders, 10× prep time)

**DOG-57** · Milestone M4 — Experiments & Metrics · scenario `kitchen_slowdown`

With `kitchen_slowdown` enabled (`factor = 10`), the kitchen stage multiplies its
prep time by 10 (implemented by
[`ChaosAwareKitchenSimulator`](../../services/kitchen/Chaos/ChaosAwareKitchenSimulator.cs)),
modelling a backed-up kitchen / degraded downstream dependency. Unlike a failure
fault, nothing throws — every order still succeeds — so this is a pure
**latency-degradation** fault. Read as a delta against the
[Experiment 1 baseline](exp1-baseline.md), same warmup + cooldown protocol.

> **What this experiment is actually testing.** A slow stage does not fail, so
> "did it error" is the wrong question. The fault-tolerance question is whether a
> latency regression in one stage is **detectable against a service-level
> objective (SLA)** and whether it stays **isolated** to the slow stage or drags
> the others down. We therefore define an explicit SLA and measure the breach
> rate under chaos vs the baseline.

## SLA definition

The SLA is set on the **per-stage kitchen processing time** — the metric the
fault directly moves — rather than on end-to-end wall-clock:

> **SLA: kitchen per-stage processing p95 < 1 000 ms.**

This is deliberate. The baseline *end-to-end* p95 is already ~13.9 s because the
unbounded 50-order burst is dominated by **queue wait**, not work
([baseline §End-to-end](exp1-baseline.md)). An e2e SLA on a burst run would be
breached by the load profile regardless of any fault, so a breach could not be
attributed to the kitchen slowdown. Putting the SLA on kitchen's own processing
time isolates the variable the fault changes: at baseline kitchen p95 = 513 ms
(**passes**), so any breach here is caused by the injected slowdown, not by
queueing or baseline flakiness.

## Method

Standard runner protocol (see [`run-experiment.sh`](../../scripts/run-experiment.sh)):
20 discarded warmup orders with chaos off → cooldown → enable `kitchen_slowdown`
with `factor = 10` → 50 measured orders → drain.

Reproduce with:

```bash
scripts/run-experiment.sh \
  --experiment-id exp5-kitchen-slowdown \
  --orders 50 \
  --scenario kitchen_slowdown \
  --params '{"factor":10}'
```

| Parameter | Value |
| --- | --- |
| Measured orders | 50 |
| Scenario | `kitchen_slowdown` (kitchen prep × 10) |
| Run tag | `exp5-kitchen-slowdown-20260610T223417Z` |
| Run window | `2026-06-10T22:34:44.196087Z` → `2026-06-10T22:37:49.339306Z` |
| Metric rows captured | 150 (50 × payment/kitchen/delivery) |

## Result 1 — SLA breach (the slowdown is detected)

Kitchen's processing time rose ~9.4× and **every single order breached** the
1 s SLA — a clean 0% → 100% breach rate against the baseline:

| Metric | Baseline | Chaos (10×) | SLA (1 000 ms) |
| --- | --- | --- | --- |
| kitchen avg (ms) | 373 | **3 494** | — |
| kitchen p50 (ms) | 368 | **3 550** | — |
| kitchen p95 (ms) | 513 | **4 792** | breached (4.8× over) |
| kitchen min / max (ms) | — | 2 035 / 4 908 | even the fastest is 2× over |
| **orders breaching SLA** | **0 / 50** | **50 / 50** | **100% breach** |

The min chaos value (2 035 ms) is already 2× the threshold, so the breach is not
a tail effect — the whole distribution moved past the SLA. The realized factor
(~9.4× on the average) is slightly under the nominal 10× because the multiplier
is applied to the inner simulator's randomized baseline draw, not a fixed
constant.

## Result 2 — Fault isolation (the slow stage stays contained)

The latency regression stayed **inside kitchen**. Payment and delivery were
untouched — same processing time and 100% success as the baseline:

| Stage | Count | Outcome | avg (ms) | baseline avg | Δ |
| --- | --- | --- | --- | --- | --- |
| payment | 50 | 50 SUCCESS | 215 | 242 | −27 |
| kitchen | 50 | 50 SUCCESS | 3 494 | 373 | **+3 121** |
| delivery | 50 | 50 SUCCESS | 532 | 523 | +9 |

A slow stage did not become a *failing* stage: 100% success, 0 retries, 0 DLQ.
The slowdown is absorbed as latency, not propagated as errors — payment and
delivery do their own work at baseline speed regardless of how long kitchen
takes. **Blast radius of the latency fault = 1 stage.**

## Result 3 — Backpressure under burst (why the SLA is per-stage, not e2e)

End-to-end wall-clock, measured here only as context, exploded far beyond the
per-stage slowdown:

| e2e metric | Baseline | Chaos (10×) |
| --- | --- | --- |
| avg (ms) | 7 959 | 87 368 |
| p95 (ms) | 13 859 | 156 942 |
| max (ms) | 14 654 | 164 641 |

This is **not** ~10× the baseline kitchen work — it is a queueing effect. When 50
orders arrive in a burst but kitchen now serves each in ~3.5 s, kitchen becomes a
throughput bottleneck and a long backlog forms; orders late in the burst wait
tens of seconds in the kitchen queue before they are even picked up. The e2e
figure therefore mixes the fault with the load profile, which is exactly why the
**SLA is defined on per-stage kitchen time** (Result 1) — that is the clean,
load-independent signal of the fault. The e2e blow-up is a legitimate *secondary*
observation: a single slow stage under sustained burst load degrades
whole-pipeline latency super-linearly via backpressure.

## Raw data

- Raw metrics CSV (committed): [`exp5-kitchen-slowdown.csv`](exp5-kitchen-slowdown.csv) — 150 rows.

CSV columns: `id, order_id, service_name, started_at, completed_at, duration_ms, retry_count, outcome`.

## Threats to validity

- **Construct validity (SLA choice):** the SLA is on per-stage kitchen time, not
  user-facing e2e, to avoid the burst-queue confound. A user-facing e2e SLA is a
  valid but different experiment — it would require pacing orders (`--rate`) and a
  paced baseline so e2e reflects work rather than queueing.
- **Conclusion validity (small N):** single 50-order run; descriptive stats, read
  as deltas against the baseline under the same protocol.
- **Internal validity (cold start / host load):** mitigated by warmup + cooldown
  and sequential runs on an idle host, identical to the baseline.

## Takeaways

- **Detectability:** a 10× kitchen slowdown moved the whole kitchen distribution
  past the 1 s SLA — **100% breach (50/50)** vs 0% at baseline. The SLA cleanly
  detects the latency fault.
- **Isolation:** payment and delivery unaffected (baseline latency, 100%
  success). A latency fault stays a latency fault and stays in one stage.
- **No errors:** slowdown ≠ failure — 0 retries, 0 DLQ; the system tolerates the
  degraded stage rather than failing the orders.
- **Backpressure (secondary):** under an unbounded burst, one slow stage inflates
  e2e latency super-linearly (avg 8 s → 87 s) through queue buildup — motivating
  load shedding / rate limiting as future work.
