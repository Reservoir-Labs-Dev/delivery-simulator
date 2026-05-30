# KIU THESIS — LINEAR PROJECT KNOWLEDGE BASE
> Fault-Tolerant Event-Driven Order Processing System
> Generated from Linear workspace: reservoir-labs · Team: Reservoir Labs (DOG)
> Defense: July 2026 · Final target: 2026-06-24 · Priority: URGENT

---

## PROJECT OVERVIEW

**Full name:** Fault-Tolerant Event-Driven Order Processing System (KIU Thesis)
**Linear URL:** https://linear.app/reservoir-labs/project/fault-tolerant-event-driven-order-processing-system-kiu-thesis-66deaaa78393
**Project ID:** be96fc93-5642-4b24-bdeb-b35ca89d2527
**Status:** Backlog (active development)
**Team:** Reservoir Labs · Issue prefix: `DOG-`
s
### Summary
KIU Bachelor Thesis. 2 students. Defense July 2026.

**Deliverable:** 30–50 page IEEE-formatted thesis + working chaos-engineering simulator.

**Architecture:**
- 4 ASP.NET Core (.NET 8) microservices: Order, Payment, Kitchen, Delivery
- Communication: RabbitMQ topic exchange (`orders.exchange`)
- Database: PostgreSQL per service (database-per-service pattern)
- Dashboard: SignalR-driven React/Blazor frontend
- Chaos engine with 5 togglable scenarios
- Full reliability stack: DLQ, exponential backoff retry, idempotent consumers

**Work model:** All tasks unassigned. Anyone picks up anything. Both team members do coding, research, and writing.

---

## MILESTONE ROADMAP

| ID | Milestone | Due Date | Progress | Description |
|----|-----------|----------|----------|-------------|
| M0 | Setup & Admin | 2026-05-03 | 97% ✅ | Repo, ADRs, Docker Compose base, scaffolded services, supervisor paperwork |
| M1 | Happy Path E2E | 2026-05-10 | 63% 🟡 | Order flows from creation through delivery with no failures. Dashboard shows live event log |
| M2 | Reliability Patterns | 2026-05-17 | 0% 🔴 | DLQ, retry with exponential backoff, idempotent consumers. Swim-lane dashboard |
| M3 | Chaos Engine | 2026-05-24 | 0% 🔴 | All 5 chaos scenarios togglable at runtime. Chaos control panel UI |
| M4 | Experiments & Metrics | 2026-05-31 | 0% 🔴 | 5 experiments + load test executed. Methodology and results sections drafted |
| M5 | Thesis Draft | 2026-06-10 | 0% 🔴 | All chapters written. Bibliography compiled. Draft sent to supervisor |
| M6 | Defense | 2026-06-24 | 0% 🔴 | Feedback addressed, slides built, demo rehearsed, thesis submitted |

---

## ISSUES BY MILESTONE

### M0 — Setup & Admin (Due: 2026-05-03 · 97% complete)

#### ✅ DONE

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-1 | Submit supervisor approval form to KIU | admin | Urgent |
| DOG-2 | Confirm KIU thesis formatting guidelines (page count, citation style, structure) | admin, docs | High |
| DOG-3 | Set up GitHub monorepo structure | infra, docs | High |
| DOG-4 | Set up branch protection and PR review rules | infra | Medium |
| DOG-5 | Set up Slack channels and daily-standup template | admin | Low |
| DOG-6 | Write ADR-001: Event-driven over REST | docs | Urgent |
| DOG-7 | Write ADR-002: RabbitMQ over Kafka / Azure Service Bus | docs | Urgent |
| DOG-8 | Write ADR-003: Topic exchange over direct/fanout | docs | Urgent |
| DOG-9 | Write ADR-004: Database-per-service over shared DB | docs | Medium |
| DOG-10 | Write ADR-005: ASP.NET Core (.NET 8) | docs | Medium |
| DOG-11 | Write ARCH-001: System overview | docs | High |
| DOG-12 | Write ARCH-002: Service contracts (events & payloads) | docs | High |
| DOG-13 | Write ARCH-003: RabbitMQ topology | docs | High |
| DOG-14 | Write ARCH-004: Data model | docs | High |
| DOG-15 | Write ARCH-005: Chaos scenarios reference | docs, chaos | High |
| DOG-16 | Set up Docker Compose base (RabbitMQ + Postgres) | infra | High |
| DOG-17 | Scaffold Order Service (.NET 8 minimal API + EF Core) | backend | High |
| DOG-18 | Scaffold Payment Service | backend | High |
| DOG-19 | Scaffold Kitchen Service | backend | High |
| DOG-20 | Scaffold Delivery Service | backend | High |
| DOG-21 | Scaffold dashboard (React or Blazor) — pick framework and bootstrap | frontend, docs | High |
| DOG-22 | Read & summarise: Hohpe & Woolf (2003) — EIP, Dead Letter Channel + Idempotent Receiver | research | High |
| DOG-23 | Read & summarise: Brewer (2000) PODC keynote + Gilbert & Lynch (2002) CAP proof | research | High |
| DOG-24 | Read & summarise: Fowler (2017) — What do you mean by Event-Driven? | research | Medium |
| DOG-25 | Read & summarise: Basiri et al. (2016) — Chaos Engineering (IEEE Software) | research | High |
| DOG-26 | Read & summarise: Wohlin et al. (2012) — Experimentation in Software Engineering | research | High |
| DOG-27 | Read: RabbitMQ Reliability Guide + AWS Exponential Backoff + MS Retry Pattern | research | Medium |
| DOG-75 | Confirm thesis language with supervisor (English-only vs Georgian + English abstract) | admin | High |
| DOG-76 | Verify committee approval status of thesis topic + supervisor (Appendix 1 form) | admin | Urgent |
| DOG-77 | Lock final thesis title with supervisor | admin | High |
| DOG-88 | Read & summarise: Richardson (microservices.io) — Database per Service + Newman | research | High |

#### 🔵 BACKLOG (remaining ~3%)

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-28 | Compile annotated bibliography (IEEE format) | research, thesis-writing | High |

---

### M1 — Happy Path E2E (Due: 2026-05-10 · 63% complete)

#### ✅ DONE

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-29 | Implement Order Service: POST /orders + publish order.created | backend | High |
| DOG-30 | Implement Payment Service consumer: order.created → payment.succeeded | backend | High |
| DOG-31 | Implement Kitchen Service consumer: payment.succeeded → order.ready | backend | High |
| DOG-32 | Implement Delivery Service consumer: order.ready → delivery.completed | backend | High |
| DOG-33 | Set up SignalR hub for OrderStatusChanged broadcasts | backend, frontend | High |

#### 🔵 BACKLOG (remaining ~37%)

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-34 | Wire all 4 services to emit OrderStatusChanged on every state transition | backend | High |
| DOG-35 | Dashboard: live event log view (M1) | frontend | High |
| DOG-36 | End-to-end smoke test: place 1 order, verify CREATED → DELIVERED | backend, experiment | High |

---

### M2 — Reliability Patterns (Due: 2026-05-17 · 0% complete) ⚠️ CURRENT FOCUS

#### 🔵 BACKLOG — All 6 issues open

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-37 | Configure DLX and DLQ per service queue | backend, infra | High |
| DOG-38 | Implement exponential-backoff retry (3 attempts: 1s, 2s, 4s) | backend | High |
| DOG-39 | Implement idempotent consumer pattern in all 4 services | backend | High |
| DOG-40 | Add retry-count and outcome fields to OrderStatusChanged events | backend | Medium |
| DOG-41 | Dashboard: swim-lane grid view | frontend | High |
| DOG-42 | Manual test: force 3 consecutive failures, verify DLQ routing + retry counts visible | experiment, backend | High |

**Issue details:**

**DOG-37 — Configure DLX and DLQ per service queue**
For each service queue, declare a DLX (`<service>.dlx`) and a DLQ (`<service>.dlq`). Set `x-dead-letter-exchange` argument on the main queue. Verify in RabbitMQ management UI that messages route to DLQ after rejection.

**DOG-38 — Implement exponential-backoff retry (3 attempts: 1s, 2s, 4s)**
On consumer exception: nack with requeue=false to a per-attempt delay queue (using TTL + DLX trick), increment retry count in message header. After 3 attempts, route to DLQ. Document the topology change in ARCH-003.

**DOG-39 — Implement idempotent consumer pattern in all 4 services**
Add `processed_event_ids (event_id uuid PK, processed_at timestamptz)` table to each service. On message receive: check if `event_id` exists; if yes, ack and skip; if no, process inside a DB transaction that also inserts the event_id row. Ensures exactly-once effect even with at-least-once delivery.

**DOG-40 — Add retry-count and outcome fields to OrderStatusChanged events**
Hub event payload now includes `retryCount` and on-failure `outcome` (success/failed/dlq). Update SignalR contract and all 4 publishers.

**DOG-41 — Dashboard: swim-lane grid view**
Replace event log with a grid: rows = orders, columns = Order/Payment/Kitchen/Delivery. Each cell shows status (color-coded: green/yellow/red) and a retry-count badge. Updates live via SignalR. Keep event-log view as a secondary tab.

**DOG-42 — Manual test: force 3 consecutive failures, verify DLQ routing + retry counts visible**
Temporarily make Payment Service throw on a specific order. Confirm: 3 retries logged, message ends in payment DLQ, dashboard shows retryCount=3 + red status, no double-processing.

---

### M3 — Chaos Engine (Due: 2026-05-24 · 0% complete)

#### 🔵 BACKLOG — All 8 issues open

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-43 | Create chaos_config table + POST /chaos/set endpoint | backend, chaos | High |
| DOG-44 | Implement chaos scenario 1: Delayed payment | backend, chaos | High |
| DOG-45 | Implement chaos scenario 2: Delivery failure loop | backend, chaos | High |
| DOG-46 | Implement chaos scenario 3: Duplicate event injection | backend, chaos | High |
| DOG-47 | Implement chaos scenario 4: Kitchen slowdown | backend, chaos | High |
| DOG-48 | Implement chaos scenario 5: Broker restart survival | chaos, infra | High |
| DOG-49 | Dashboard: Chaos Control Panel UI | frontend, chaos | High |
| DOG-50 | Manual smoke test: each chaos scenario fires correctly | chaos, experiment | High |

**Issue details:**

**DOG-43 — Create chaos_config table + POST /chaos/set endpoint**
Shared `chaos_config` table. Columns: `name text PK, enabled bool, params jsonb, updated_at`. Endpoint: `POST /chaos/set { name, enabled, params }`. Each service reads chaos_config on every message.

**DOG-44 — Scenario 1: Delayed payment**
Payment Service reads `delayed_payment` config. If enabled, sleep `params.delay_ms` before processing (default 5000ms). Simulates slow external gateway.

**DOG-45 — Scenario 2: Delivery failure loop**
Delivery Service reads `delivery_failure_loop` config. If enabled, always throws `SimulatedDeliveryException`. Forces 3 retries → DLQ. Tests DLQ + retry pattern under sustained failure.

**DOG-46 — Scenario 3: Duplicate event injection**
Order Service reads `duplicate_events` config. If enabled, publishes `order.created` N times (default N=3) with the SAME event_id. Tests idempotent-consumer: downstream must process exactly once.

**DOG-47 — Scenario 4: Kitchen slowdown**
Kitchen Service reads `kitchen_slowdown` config. If enabled, multiplies prep time by `params.factor` (default 10x). Measures SLA breach count vs baseline.

**DOG-48 — Scenario 5: Broker restart survival**
Procedure-based (not service code). With durable queues + persistent messages configured, document and script a `docker restart rabbitmq` mid-flow. Verify in-flight orders complete after broker comes back. Script: `/scripts/chaos-broker-restart.sh`.

**DOG-49 — Dashboard: Chaos Control Panel UI**
5 toggle switches (one per scenario) + param inputs. Calls `POST /chaos/set` on toggle. Active scenarios shown as red warning banner across top. Live-reflects current chaos_config state.

**DOG-50 — Manual smoke test**
For each of the 5 scenarios: enable via UI, place orders, observe expected behavior in dashboard + DBs + RabbitMQ UI. Document one screenshot per scenario in `/docs/chaos/screenshots/`.

---

### M4 — Experiments & Metrics (Due: 2026-05-31 · 0% complete)

#### 🔵 BACKLOG — All 8 issues open

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-51 | Create metrics table + writer in each service | backend, experiment | High |
| DOG-52 | Build experiment runner script (50 orders, configurable scenario) | experiment, backend | High |
| DOG-53 | Experiment 1: Happy path baseline (50 orders, no chaos) | experiment | High |
| DOG-54 | Experiment 2: Payment failure (50 orders, delayed_payment enabled) | experiment | High |
| DOG-55 | Experiment 3: Delivery failure loop (50 orders, all delivery fails) | experiment | High |
| DOG-56 | Experiment 4: Duplicate storm (50 orders × 3 duplicates each) | experiment | High |
| DOG-57 | Experiment 5: Kitchen slowdown (50 orders, 10x prep time) | experiment | High |
| DOG-58 | Load test: 100 concurrent orders | experiment, infra | High |
| DOG-59 | Draft Methodology chapter (experimental design + threats to validity) | thesis-writing | High |
| DOG-60 | Draft Results chapter (tables, charts, interpretation) | thesis-writing | High |

**Issue details:**

**DOG-51 — Metrics table + writer**
`metrics (id, order_id, service_name, started_at, completed_at, retry_count, outcome)`. Each service writes a row at end of each message handler (success or failure). Add export endpoint: `GET /metrics/export.csv`.

**DOG-52 — Experiment runner script**
`/scripts/run-experiment.py` (or .sh): takes scenario name + N orders, sets chaos_config via API, posts N orders with controlled timing, waits for completion, exports metrics CSV with experiment label. Reusable for all 5 experiments.

**DOG-53 — Experiment 1: Baseline**
All chaos disabled. Compute per-stage avg/p50/p95 processing time. Save raw CSV + summary table to `/docs/experiments/exp1-baseline.md`. Control for all other experiments.

**DOG-54 — Experiment 2: Payment failure**
Enable delayed_payment (delay_ms=5000). 50 orders. Measure: avg retry count, recovery rate, e2e time vs baseline. Save to `/docs/experiments/exp2-payment-delay.md`.

**DOG-55 — Experiment 3: Delivery failure loop**
Enable delivery_failure_loop. 50 orders. Measure: DLQ message count, % orders ending in DLQ, retry count distribution. Expected: ~100% DLQ. Save to `/docs/experiments/exp3-delivery-dlq.md`.

**DOG-56 — Experiment 4: Duplicate storm**
Enable duplicate_events with N=3. 50 orders. Measure: total messages sent (~150), total messages processed (must be 50), zero double-side-effects in DB. Validates idempotency. Save to `/docs/experiments/exp4-duplicate-storm.md`.

**DOG-57 — Experiment 5: Kitchen slowdown**
Enable kitchen_slowdown (factor=10). 50 orders. Define SLA threshold (e.g. p95 e2e < 5s). Measure SLA breach count vs baseline. Save to `/docs/experiments/exp5-kitchen-slowdown.md`.

**DOG-58 — Load test**
100 concurrent POSTs (k6, Bombardier, or hand-rolled async Python). Measure: throughput (orders/s), error rate, queue depths in RabbitMQ over time. Save to `/docs/experiments/load-test.md`.

**DOG-59 — Methodology chapter**
Experimental setup, hypothesis per experiment, independent/dependent variables, measurement procedure, threats to validity (single-host, simulated load). Reference Wohlin et al. (2012).

**DOG-60 — Results chapter**
All 6 experiments. For each: hypothesis recap, results table, chart (bar/box/timeline), interpretation. Cross-experiment summary table. Use matplotlib; export PNGs to `/docs/thesis/figures/`.

---

### M5 — Thesis Draft (Due: 2026-06-10 · 0% complete)

#### 🔵 BACKLOG — All 11 issues open

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-61 | Draft Introduction chapter (~3–5 pages) | thesis-writing | High |
| DOG-62 | Draft Literature Review: Event-driven architecture + EIP (~5–7 pages) | thesis-writing | High |
| DOG-63 | Draft Literature Review: CAP, eventual consistency, fault tolerance (~3–5 pages) | thesis-writing | High |
| DOG-64 | Draft Literature Review: Chaos Engineering (~3–5 pages) | thesis-writing | High |
| DOG-65 | Draft Implementation chapter (~6–10 pages) | thesis-writing | High |
| DOG-66 | Draft Conclusion + Future Work chapter (~2–3 pages) | thesis-writing | High |
| DOG-67 | Compile thesis into single document with IEEE formatting | thesis-writing | High |
| DOG-68 | Send draft to supervisor + log feedback request in Linear | admin, thesis-writing | High |
| DOG-78 | Build title page per KIU Annex 4 (exact formatting) | thesis-writing | High |
| DOG-79 | Write Declaration page (exact required wording) | thesis-writing | High |
| DOG-80 | Write Abstract (150–200 words) + keywords | thesis-writing | High |
| DOG-81 | Build Table of Contents with Roman + Arabic page numbering | thesis-writing | High |
| DOG-82 | Compile Appendices section | thesis-writing | High |
| DOG-83 | Apply final formatting pass (font, size, spacing, margins) | thesis-writing | High |

**Key formatting rules (KIU Article 4.3):**
- Length: 30–50 pages
- Font: Times New Roman (or Arial / Calibri / Sylfaen), 12pt
- Line spacing: 1.5
- Margins: left 3cm, right 1.5cm, top 2.5cm, bottom 2.5cm
- Citation style: IEEE (`[1]`, `[2]`...)
- Abstract: 150–200 words, NO citations allowed, starts on a new page
- Page numbering: Roman numerals for front matter, Arabic from body (restart at 1)
- TOC must include all chapters, sections, subsections

**Chapter size targets:**
- Introduction: 3–5 pages
- Lit Review (EDA + EIP): 5–7 pages
- Lit Review (CAP + chaos): 3–5 pages each
- Implementation: 6–10 pages
- Conclusion + Future Work: 2–3 pages
- Results: tables + charts per experiment

**Appendices to include:**
- A: Full event payload JSON schemas (all 6 events)
- B: Full PostgreSQL DDL per service + shared chaos_config + metrics
- C: Raw experiment data (CSV exports)
- D: RabbitMQ topology diagram (full version)
- E: Selected source code excerpts

---

### M6 — Defense (Due: 2026-06-24 · 0% complete)

#### 🔵 BACKLOG — All 8 issues open

| ID | Title | Labels | Priority |
|----|-------|--------|----------|
| DOG-69 | Address supervisor feedback round 1 | thesis-writing | Urgent |
| DOG-70 | Build defense slide deck (~15–20 slides) | defense | High |
| DOG-71 | Write demo script (5–7 min live demo) | defense | High |
| DOG-72 | Rehearse defense (3 full run-throughs) | defense | High |
| DOG-73 | Final thesis submission to KIU | admin | Urgent |
| DOG-74 | Defense day: backup demo recording + offline copies | defense | High |
| DOG-84 | Confirm submission filename convention for two-author thesis | admin, defense | High |
| DOG-85 | Submit thesis + presentation materials ONE WEEK before defense | admin, defense | Urgent |
| DOG-86 | Run own plagiarism check before official submission | admin, thesis-writing | High |
| DOG-87 | Confirm defense scheduling and committee composition | admin, defense | Urgent |

**Critical deadlines:**
- Thesis + presentation materials must be submitted ONE WEEK before defense date (not same day)
- Plagiarism review happens in this window; if found in FINAL version = right to resubmit is lost
- Run own check (Turnitin via KIU library, Grammarly Premium, or Copyleaks) at least 3 days before official deadline
- Filename convention: `Bachelor's Thesis – Student's Full Name` — confirm two-author format with School before submission

**Defense slide deck structure (15–20 slides):**
Title → Problem → Research questions → Architecture overview → Reliability patterns → Chaos engine → Experimental setup → Results highlights (1 slide per experiment) → Conclusions → Demo placeholder → Q&A

**Demo script (5–7 min):**
1. Start system
2. Place baseline order, show swim-lane
3. Enable duplicate-storm chaos → show idempotency holding
4. Enable delivery-failure-loop → show DLQ filling

**Likely Q&A questions to prep:**
- Why CAP — AP over CP?
- Why RabbitMQ over Kafka?
- Threats to validity (single-host, simulated load, no production data)
- Why no circuit breaker?
- What is exponential backoff with jitter?

---

## ARCHITECTURE REFERENCE

### Event Flow (Happy Path)
```
POST /orders
    → Order Service persists + publishes order.created
        → Payment Service consumes → persists → publishes payment.succeeded
            → Kitchen Service consumes → persists → publishes order.ready
                → Delivery Service consumes → persists → publishes delivery.completed
                    → SignalR hub broadcasts OrderStatusChanged at each step
                        → React/Blazor dashboard updates live
```

### RabbitMQ Topology
- **Exchange:** `orders.exchange` (topic)
- **Routing key convention:** `<entity>.<event>` (e.g. `order.created`, `payment.succeeded`)
- **Per service:** one durable queue + DLX (`<service>.dlx`) + DLQ (`<service>.dlq`)
- **Retry policy:** 3 attempts at 1s / 2s / 4s exponential backoff via TTL + DLX trick
- **After 3 failures:** message routed to DLQ
- **Prefetch count:** documented in ARCH-003

### Events Catalog
| Event | Routing Key | Producer | Consumer(s) |
|-------|-------------|----------|-------------|
| order.created | order.created | Order Service | Payment Service |
| payment.succeeded | payment.succeeded | Payment Service | Kitchen Service |
| payment.failed | payment.failed | Payment Service | (DLQ / future) |
| order.ready | order.ready | Kitchen Service | Delivery Service |
| delivery.completed | delivery.completed | Delivery Service | — |
| delivery.failed | delivery.failed | Delivery Service | (DLQ / future) |

All events carry an `event_id (UUID)` as the idempotency key.

### Data Models (per service)
Every service has:
- Its own PostgreSQL schema
- `processed_event_ids (uuid PK, processed_at timestamptz)` — idempotency table
- `chaos_config (id, name, enabled bool, params jsonb, updated_at)` — chaos state
- `metrics (id, order_id, service_name, started_at, completed_at, retry_count, outcome)` — experiment data

### Idempotency Pattern
```
On message receive:
  1. Check processed_event_ids WHERE event_id = incoming.event_id
  2. If exists → ack, skip (already processed)
  3. If not → open DB transaction:
       a. Do business logic
       b. INSERT INTO processed_event_ids (event_id, processed_at)
       c. Commit
  → Guarantees exactly-once effect under at-least-once delivery
```

### Chaos Scenarios Reference
| # | Name | Config Key | Service | What It Tests |
|---|------|------------|---------|---------------|
| 1 | Delayed payment | `delayed_payment` | Payment | Retry + backoff under slow dependency |
| 2 | Delivery failure loop | `delivery_failure_loop` | Delivery | DLQ routing under sustained failure |
| 3 | Duplicate event injection | `duplicate_events` | Order | Idempotent consumer (N copies, 1 effect) |
| 4 | Kitchen slowdown | `kitchen_slowdown` | Kitchen | SLA breach measurement |
| 5 | Broker restart survival | (script) | Infra | Durable queue + message persistence |

### ADR Summary
| ADR | Decision | Rationale |
|-----|----------|-----------|
| ADR-001 | Event-driven over REST | Loose coupling, independent deployability, meaningful chaos scenarios |
| ADR-002 | RabbitMQ over Kafka/Azure SB | Built-in DLX, management UI, no cloud lock-in, Docker-friendly |
| ADR-003 | Topic exchange over direct/fanout | Routing-key flexibility for future event types |
| ADR-004 | Database-per-service | Bounded-context isolation, no cross-service coupling |
| ADR-005 | ASP.NET Core .NET 8 | Team expertise, strong ecosystem, async/await maturity |

---

## BIBLIOGRAPHY (IEEE format — from project brief)

Key sources referenced across the thesis:
- Hohpe & Woolf (2003) — Enterprise Integration Patterns (Dead Letter Channel p.111, Idempotent Receiver p.549)
- Fowler (2017) — What do you mean by "Event-Driven"?
- Brewer (2000) — PODC keynote (CAP theorem origin)
- Gilbert & Lynch (2002) — CAP proof
- Basiri et al. (2016) — Chaos Engineering (IEEE Software)
- Wohlin et al. (2012) — Experimentation in Software Engineering
- Richardson (microservices.io) — Database per Service pattern
- Newman — Building Microservices (data chapter)
- RabbitMQ Reliability Guide (2024)
- AWS — Exponential Backoff and Jitter (2024)
- Microsoft — Retry Pattern (2024)

Full BibTeX: `/docs/thesis/bibliography.bib`
Human-readable IEEE list: `/docs/thesis/bibliography.md`

---

## ISSUE STATUS SUMMARY

| Status | Count |
|--------|-------|
| ✅ Done | 32 |
| 🔵 Backlog | 55 |
| 🟡 In Progress | 0 |
| **Total** | **87** |

### Open issues by milestone
| Milestone | Open | Total |
|-----------|------|-------|
| M0 | 1 | 31 |
| M1 | 3 | 8 |
| M2 | 6 | 6 |
| M3 | 8 | 8 |
| M4 | 10 | 10 |
| M5 | 14 | 14 |
| M6 | 10 | 10 |

### Open issues by label
| Label | Open issues |
|-------|-------------|
| backend | DOG-34, 37, 38, 39, 40, 42, 51, 52 |
| frontend | DOG-35, 41, 49 |
| chaos | DOG-43, 44, 45, 46, 47, 48, 49, 50 |
| experiment | DOG-36, 42, 50, 51, 52, 53, 54, 55, 56, 57, 58 |
| thesis-writing | DOG-59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 78, 79, 80, 81, 82, 83, 86 |
| admin / defense | DOG-28, 70, 71, 72, 73, 74, 84, 85, 86, 87 |
| infra | DOG-48, 58 |
| research | DOG-28 |

---

## FILE/PATH CONVENTIONS (from issue descriptions)

```
/docs/
  adr/          ADR-001 through ADR-005 (+ ADR-006 frontend framework)
  arch/         ARCH-001 through ARCH-005
  admin/        title.md, submission-format.md, supervisor confirmation
  research/     eip-summary.md, cap-summary.md, microservices-data-summary.md, reliability-notes.md
  thesis/       bibliography.bib, bibliography.md, thesis-draft-v1.pdf
                figures/  (matplotlib PNGs for results chapter)
  experiments/  exp1-baseline.md, exp2-payment-delay.md, exp3-delivery-dlq.md,
                exp4-duplicate-storm.md, exp5-kitchen-slowdown.md, load-test.md
  chaos/        screenshots/  (one per scenario)
  test/         e2e-smoke.md
/services/
  order/
  payment/
  kitchen/
  delivery/
/dashboard/
/docker/
/scripts/
  run-experiment.py (or .sh)
  chaos-broker-restart.sh
```
