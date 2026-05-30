# Chaos Engineering — Research Summary

Source: Basiri, A., Behnam, N., de Rooij, R., Hochstein, L., Kosewski, L., Reynolds, J., and Rosenthal, C., "Chaos Engineering", IEEE Software, vol. 33, no. 3, pp. 35–41, May–June 2016. DOI: 10.1109/MS.2016.60.

---

## Background

Basiri et al. define chaos engineering as the discipline of experimenting on a distributed system to build confidence in its ability to withstand turbulent conditions in production. The paper comes out of Netflix, where the scale and complexity of the system made it impossible to reason about failure modes through code review or unit testing alone. The core argument is that distributed systems have emergent failure behaviour — properties that only appear when the whole system is under stress — and the only way to find them is to deliberately break things in a controlled way and observe what happens.

The paper defines four principles that separate disciplined chaos engineering from just randomly breaking things.

---

## The four principles

### 1. Build a hypothesis around steady-state behaviour

Before running any experiment, define what "normal" looks like in terms of measurable output. Basiri et al. use Netflix's SPS (Streams Per Second) as their steady-state metric — the rate at which users successfully start watching videos. The hypothesis is: *this metric will not change significantly when we inject the failure*.

The key insight is that you measure *system output*, not internal component state. You don't check "is this server healthy" — you check "are users getting what they came for". If the output stays stable despite the injected failure, you have evidence the system is resilient to that failure. If it drops, you have found a real weakness before a real outage found it for you.

### 2. Vary real-world events

The failures you inject should reflect failures that actually happen in production. Basiri et al. list examples: server terminations, hard disk failures, malformed responses, network latency spikes, region-level outages. The point is to avoid testing against unrealistic scenarios — a chaos experiment that only tests a single server crash tells you little about how your system handles cascading failures or degraded dependencies.

Experiments should be ranked by estimated likelihood and potential impact, focusing first on failures that are probable or would be catastrophic.

### 3. Run experiments in production

This is the most controversial principle. Basiri et al. argue that staging environments never fully replicate production behaviour — traffic patterns, data shapes, and service interactions are always slightly different. A system that is resilient in staging may still fail in production due to conditions that only exist at scale.

For our system, we do not run in production (it is an academic project). We run in a local Docker Compose environment. This is acknowledged as a threat to external validity in our experiment design — our results hold for our simulated environment but cannot be directly generalised to a real food delivery platform at production scale.

### 4. Automate experiments to run continuously

Basiri et al. argue that a chaos experiment run once gives you a snapshot. The system changes over time — new code, new dependencies, configuration changes — and a failure mode that did not exist last month may be introduced by today's deployment. Continuous, automated chaos experiments turn resilience verification into an ongoing property rather than a one-time check.

For our system, automation is partial. We run experiments manually via a script (DOG-52) for the thesis. The infrastructure for continuous execution is out of scope.

---

## Mapping to our experiment design

| Basiri et al. principle | Our implementation |
|---|---|
| Steady-state hypothesis | Baseline experiment (Exp 1, DOG-53): 50 orders, no chaos, measure throughput and end-to-end latency. This is our steady state. |
| Vary real-world events | Five chaos scenarios modelling realistic failures: payment delay, delivery failure, duplicate messages, kitchen slowdown, broker restart. |
| Run in production | We run in Docker Compose locally — acknowledged as an external validity threat in the thesis methodology chapter. |
| Automate | Experiment runner script (DOG-52) automates order placement and metrics collection. Manual scenario activation via `/chaos/set`. Full continuous automation is future work. |

The steady-state hypothesis for each of our experiments follows the Basiri et al. pattern directly:

- **Exp 1 (happy path):** H₀: all 50 orders complete end-to-end within baseline latency with zero DLQ entries.
- **Exp 2 (delayed payment):** H₀: orders still complete despite payment delay; retry mechanism absorbs the failure.
- **Exp 3 (delivery failure):** H₀: failed deliveries are routed to DLQ; no messages are lost; pipeline does not hang.
- **Exp 4 (duplicate injection):** H₀: idempotent consumers process each order exactly once despite duplicate delivery.
- **Exp 5 (kitchen slowdown):** H₀: throughput degrades gracefully; no errors, no DLQ activity; orders complete eventually.

