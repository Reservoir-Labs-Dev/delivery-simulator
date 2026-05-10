# Experimental Design — Research Summary

Source: Wohlin, C., Runeson, P., Höst, M., Ohlsson, M. C., Regnell, B., and Wesslén, A., *Experimentation in Software Engineering*, Springer, 2012. DOI: 10.1007/978-3-642-29044-2.

---

## What the book is about

Wohlin et al. is the standard reference for rigorous empirical research in software engineering. It covers how to design, run, and report controlled experiments — including how to formulate hypotheses, choose variables, and identify what can go wrong with your conclusions. We use it to structure our experiment chapter and to make sure our five experiments are defensible in front of a thesis committee.

---

## Hypothesis formulation

Wohlin et al. describe hypotheses in the standard null/alternative form:

- **H₀ (null hypothesis):** the independent variable has no effect on the dependent variable. This is what you assume to be true until the data says otherwise.
- **H₁ (alternative hypothesis):** the independent variable does have an effect.

The goal of the experiment is to either reject H₀ (you found an effect) or fail to reject it (you found no evidence of an effect — not the same as proving no effect exists).

Hypotheses must be stated before the experiment, not after looking at the data. Formulating hypotheses post-hoc to match your results is a threat to conclusion validity.

---

## Independent and dependent variables

**Independent variable (IV):** what you deliberately change. In our experiments, this is always the chaos scenario — which failure mode is active (or none, for the baseline).

**Dependent variable (DV):** what you measure to see if the IV had an effect. In our experiments these are: end-to-end order latency, throughput (orders/second), retry count, DLQ entry count, and duplicate processing rate.

**Confounding factors:** variables that affect the DV but are not the IV you intended to study. Wohlin et al. emphasise identifying these in advance. In our case: Docker resource limits on the test machine, RabbitMQ broker load, background system processes, and network conditions on the host. We mitigate by running each experiment on the same machine with all other applications closed.

---

## Threats to validity

Wohlin et al. identify four categories of validity threats. These must be discussed in the thesis methodology chapter.

### Conclusion validity
Can you trust the statistical relationship you found? Threats include small sample sizes and unreliable measurements. In our case: 50 orders per experiment is a small sample. We report descriptive statistics (mean, min, max, p95 latency) rather than significance tests, which is appropriate for an exploratory study at this scale.

### Internal validity
Are you sure the IV caused the change in the DV, and not something else? Threats include uncontrolled confounding factors. In our case: if the test machine is under CPU pressure during experiment 4 (kitchen slowdown), the observed latency increase may reflect host load rather than the chaos scenario. Mitigation: run experiments sequentially with cooldown periods.

### Construct validity
Does your measurement actually measure what you claim? Threats include measuring a proxy metric that does not reflect the real phenomenon. In our case: we measure `processing_ms` per event as a proxy for "service resilience". This measures speed, not correctness. We also track DLQ entries and retry counts as correctness proxies.

### External validity
Can you generalise your results beyond the specific experiment context? This is our weakest area. Our experiments run on a single-machine Docker Compose setup with simulated load. Real food delivery systems operate at orders of magnitude higher scale, with real network conditions, real hardware failures, and real user behaviour. Our results demonstrate the *patterns* work under controlled conditions — they do not demonstrate production-grade resilience. This must be stated explicitly in the thesis.

---

## Applying this to our five experiments

| Experiment | H₀ | IV | DVs | Key validity threat |
|---|---|---|---|---|
| Exp 1 — Baseline | All orders complete, zero DLQ | None (control) | Latency, throughput | Conclusion: small N |
| Exp 2 — Delayed payment | Orders complete despite delay; retries absorb failure | Payment delay (5 s) | Retry count, latency, DLQ entries | Internal: host CPU load |
| Exp 3 — Delivery failure | All messages reach DLQ; none lost | Delivery always fails | DLQ depth, message loss | Construct: DLQ depth as loss proxy |
| Exp 4 — Duplicate injection | Each order processed exactly once | 3× duplicate delivery | Duplicate skip count, DB row count | Internal: timing of duplicate delivery |
| Exp 5 — Kitchen slowdown | Throughput degrades; no errors | 10× prep time | Queue depth, throughput, error rate | External: single-machine resource limits |
