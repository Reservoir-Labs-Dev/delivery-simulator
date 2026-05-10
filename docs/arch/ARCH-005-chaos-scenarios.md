# ARCH-005: Chaos Scenarios Reference

**Status:** Living document — update if scenario behaviour changes during implementation
**Last updated:** 2026-05-04

---

## 1. Overview

The chaos engine injects five failure scenarios at runtime to validate the system's fault-tolerance patterns under controlled conditions. Scenarios are activated and deactivated via `POST /chaos/set` on any service without restarting containers. The current chaos state is stored in the `chaos_config` table (one row per service) and read by the consumer on every message.

Scenarios are designed to be observable: each one produces a distinct signature in the RabbitMQ management UI, the `metrics.events` table, and the dashboard swim-lane view.

---

## 2. Chaos configuration API

Every service exposes:

```
POST /chaos/set
Content-Type: application/json

{
  "scenario": "delayed_payment",   // scenario key, or "none" to clear
  "parameters": {                  // scenario-specific, optional
    "delayMs": 5000
  }
}

GET /chaos/current
→ { "scenario": "delayed_payment", "parameters": { "delayMs": 5000 }, "activeSince": "..." }
```

The dashboard Chaos Control Panel (DOG-49) calls these endpoints. Scenario keys are defined in section 3.

---

## 3. Scenario catalogue

### Scenario 1 — Delayed payment (`delayed_payment`)

**Target service:** PaymentService
**Pattern exercised:** Exponential backoff retry, timeout handling
**Chaos key:** `delayed_payment`

**Behaviour:** PaymentService artificially delays processing of `order.created` by a configurable duration before attempting payment. If the delay exceeds the consumer's processing timeout, the message is nacked and enters the retry queue.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `delayMs` | `int` | `5000` | Milliseconds to sleep before processing. Set above the consumer timeout to force a nack. |

**What to observe:**
- Messages accumulate in `payment.retry.1`, `payment.retry.2`, `payment.retry.3` queues in sequence.
- `metrics.events` rows show `outcome = RETRIED` with increasing `attempt_number`.
- Dashboard shows orders stuck in `PAYMENT_PROCESSING` for longer than baseline.
- After retries are exhausted: message appears in `payment.dlq`.

**Experiment:** Experiment 2 (DOG-54) — 50 orders, `delayed_payment` enabled.

---

### Scenario 2 — Delivery failure loop (`delivery_failure_loop`)

**Target service:** DeliveryService
**Pattern exercised:** DLQ routing, retry exhaustion, idempotent consumer
**Chaos key:** `delivery_failure_loop`

**Behaviour:** DeliveryService unconditionally fails every `order.ready` message regardless of content, simulating a permanently broken downstream dependency. All messages exhaust their 3 retries and are routed to `delivery.dlq`.

**Parameters:** None.

**What to observe:**
- `delivery.retry.1`, `.2`, `.3` queues cycle through messages.
- All messages eventually land in `delivery.dlq`.
- `metrics.events` shows `outcome = DEAD_LETTERED` for all delivery events.
- Dashboard shows all orders terminal at `DELIVERY_FAILED`.
- `delivery.dlq` depth equals the number of orders placed.

**Experiment:** Experiment 3 (DOG-55) — 50 orders, all delivery fails.

---

### Scenario 3 — Duplicate event injection (`duplicate_injection`)

**Target service:** Any / all services (injected at publisher level)
**Pattern exercised:** Idempotent consumer, `processed_event_ids` deduplication
**Chaos key:** `duplicate_injection`

**Behaviour:** When a service publishes an event, it immediately re-publishes the same message (identical `eventId`, identical payload) N additional times to the same routing key on `orders.exchange`. Consumers must detect and skip the duplicates via the idempotency check.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `duplicateCount` | `int` | `2` | Number of extra copies to publish per original event. `2` means 3 total deliveries of each message. |

**What to observe:**
- Consumer logs show `Skipping duplicate event {EventId}` for duplicate deliveries.
- `metrics.events` shows `outcome = SKIPPED` for duplicates, `outcome = PROCESSED` for the original.
- Domain state (e.g. `payments.payment_records`) has exactly one row per order despite multiple deliveries.
- No duplicate `OrderStatusChanged` notifications reach the dashboard.

**Experiment:** Experiment 4 (DOG-56) — 50 orders × 3 total deliveries each.

---

### Scenario 4 — Kitchen slowdown (`kitchen_slowdown`)

**Target service:** KitchenService
**Pattern exercised:** Backpressure, queue depth under sustained load, throughput degradation
**Chaos key:** `kitchen_slowdown`

**Behaviour:** KitchenService multiplies its simulated preparation time by a configurable factor. Under load this causes `kitchen.queue` to build up depth as the consumer falls behind the publication rate.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `slowdownFactor` | `int` | `10` | Multiplier applied to the base preparation time. Factor of 10 means a 500 ms prep takes 5 000 ms. |

**What to observe:**
- `kitchen.queue` depth climbs steadily during the experiment run.
- Message rate on `kitchen.queue` (deliver rate) drops significantly below the publish rate.
- `metrics.events` shows high `processing_ms` values for kitchen events.
- Dashboard shows orders piling up in `KITCHEN_PREPARING`.
- No retry or DLQ activity — this scenario tests throughput degradation, not failure recovery.

**Experiment:** Experiment 5 (DOG-57) — 50 orders, 10× prep time.

---

### Scenario 5 — Broker restart survival (`broker_restart`)

**Target service:** Infrastructure (RabbitMQ container)
**Pattern exercised:** Durable queues, persistent messages, consumer reconnection
**Chaos key:** `broker_restart` _(manual — not triggered via `/chaos/set`)_

**Behaviour:** The RabbitMQ container is restarted mid-experiment using `docker compose restart rabbitmq`. Services lose their AMQP connections. The scenario validates that:

1. In-flight messages are not lost (durable queues + persistent messages).
2. Services detect the dropped connection via heartbeat timeout and reconnect automatically.
3. Processing resumes from where it left off with no duplicate processing (idempotency).

**Parameters:** None. Triggered manually during the experiment.

**What to observe:**
- Services log AMQP connection errors immediately after restart.
- Services log successful reconnection within the heartbeat timeout window (typically 10–30 s).
- Queue depths before and after restart match — no messages lost.
- `metrics.events` shows no gap in `recorded_at` beyond the reconnection window.
- No messages appear in DLQs as a result of the restart alone.

**Experiment:** Covered by chaos scenario 5 smoke test (DOG-50). Not a standalone numbered experiment — broker restart survival is validated as part of the M3 manual smoke tests.

---

## 4. chaos_config table

Each service reads its current chaos state from its own schema:

```sql
CREATE TABLE <schema>.chaos_config (
    id          INT         PRIMARY KEY DEFAULT 1,  -- singleton row
    scenario    TEXT        NOT NULL DEFAULT 'none',
    parameters  JSONB       NOT NULL DEFAULT '{}',
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT  single_row CHECK (id = 1)
);

INSERT INTO <schema>.chaos_config (id, scenario, parameters)
VALUES (1, 'none', '{}')
ON CONFLICT DO NOTHING;
```

The `POST /chaos/set` handler does an upsert on this row. The consumer reads it at the start of each message handler (one extra DB read per message — acceptable at our scale).

---

## 5. Scenario interaction matrix

Scenarios are designed to be activated one at a time. Running multiple scenarios simultaneously is not tested and may produce ambiguous metrics.

| Scenario | Retries triggered | DLQ activity | Idempotency exercised | Throughput impact |
|---|---|---|---|---|
| 1 — Delayed payment | yes | if delay > timeout × 3 | no | moderate |
| 2 — Delivery failure loop | yes | yes (all messages) | yes (on redelivery) | low |
| 3 — Duplicate injection | no | no | yes (all duplicates) | low |
| 4 — Kitchen slowdown | no | no | no | high |
| 5 — Broker restart | yes (reconnect) | no | yes (reconnect) | spike |

---

## 6. Experiment runner integration

The experiment runner script (DOG-52) sets the chaos scenario before placing orders and clears it after:

```bash
# Set scenario
curl -X POST http://localhost:5203/chaos/set \
  -H 'Content-Type: application/json' \
  -d '{"scenario":"delayed_payment","parameters":{"delayMs":5000}}'

# Run 50 orders
./run-experiment.sh --count 50 --experiment-id exp-2-delayed-payment

# Clear scenario
curl -X POST http://localhost:5203/chaos/set \
  -H 'Content-Type: application/json' \
  -d '{"scenario":"none","parameters":{}}'
```

All `metrics.events` rows written during the run carry the `experiment_id` and `chaos_scenario` values for later analysis.

---

## Related documents

- ARCH-003 — RabbitMQ topology (retry queues, DLX, DLQ)
- ARCH-004 — Data model (`chaos_config` table, `metrics.events` table)
- ARCH-002 — Service contracts (`reason` field values: `CHAOS_INJECTED`)
- ADR-001 — Why event-driven (chaos scenarios only meaningful in message-driven system)
