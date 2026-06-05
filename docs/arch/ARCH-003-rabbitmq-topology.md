# ARCH-003: RabbitMQ Topology

**Status:** Living document — update if queues or bindings change during implementation
**Last updated:** 2026-06-05 (DOG-37: per-service DLX replaces the shared `orders.dlx`)

---

## 1. Overview

This document defines the complete RabbitMQ topology: exchanges, queues, bindings, dead-letter configuration, and retry behaviour. It is the authoritative reference for infrastructure setup and for the consumer startup code in each service.

All entities are declared durable. All messages are published as persistent. This ensures the broker can survive a restart without losing in-flight messages (see reliability notes and chaos scenario 5).

---

## 2. Exchanges

| Exchange | Type | Durable | Purpose |
|---|---|---|---|
| `orders.exchange` | `topic` | yes | Primary exchange. All services publish here. |
| `payment.dlx` | `fanout` | yes | PaymentService dead-letter exchange. Routes `payment.queue` failures to `payment.dlq`. |
| `kitchen.dlx` | `fanout` | yes | KitchenService dead-letter exchange. Routes `kitchen.queue` failures to `kitchen.dlq`. |
| `delivery.dlx` | `fanout` | yes | DeliveryService dead-letter exchange. Routes `delivery.queue` failures to `delivery.dlq`. |
| `order.dlx` | `fanout` | yes | OrderService status-consumer dead-letter exchange. Routes `order.status.queue` failures to `order.status.dlq`. |

Each service owns a **dedicated** dead-letter exchange (`<service>.dlx`), declared by that service's consumer with exactly one DLQ bound to it. This gives **failure isolation**: a message that dies in `payment.queue` lands only in `payment.dlq`, never in another service's DLQ.

A shared fanout DLX would have broken this — fanout ignores routing keys, so a single shared `orders.dlx` with all four DLQs bound would copy every dead-lettered message into *all four* DLQs at once, making per-service inspection meaningless.

Each `<service>.dlx` is a fanout with a single bound DLQ, so the binding does not need a routing key. RabbitMQ preserves the original routing key in the `x-death` header for inspection and replay.

---

## 3. Queues

### 3.1 Primary queues

Each queue has an `x-dead-letter-exchange` argument pointing to its own service DLX (`<service>.dlx`) (see section 5).

| Queue | Binding exchange | Routing key | Dead-letters to | Consumer service |
|---|---|---|---|---|
| `payment.queue` | `orders.exchange` | `order.created` | `payment.dlx` | PaymentService |
| `kitchen.queue` | `orders.exchange` | `payment.succeeded` | `kitchen.dlx` | KitchenService |
| `delivery.queue` | `orders.exchange` | `order.ready` | `delivery.dlx` | DeliveryService |
| `order.status.queue` | `orders.exchange` | (5 status keys) | `order.dlx` | OrderService |

Queue declaration arguments (same shape for all, DLX differs per service):

```
x-dead-letter-exchange: <service>.dlx
x-queue-type:           classic
durable:                true
```

### 3.2 Retry queues

Each primary queue has a corresponding retry queue used to implement exponential backoff without blocking the primary queue. The retry queue has a per-message TTL; when the TTL expires the message is routed back to the primary queue via its dead-letter exchange argument.

| Queue | TTL | Routes back to |
|---|---|---|
| `payment.retry.1` | 1 000 ms | `orders.exchange` / `order.created` |
| `payment.retry.2` | 2 000 ms | `orders.exchange` / `order.created` |
| `payment.retry.3` | 4 000 ms | `orders.exchange` / `order.created` |
| `kitchen.retry.1` | 1 000 ms | `orders.exchange` / `payment.succeeded` |
| `kitchen.retry.2` | 2 000 ms | `orders.exchange` / `payment.succeeded` |
| `kitchen.retry.3` | 4 000 ms | `orders.exchange` / `payment.succeeded` |
| `delivery.retry.1` | 1 000 ms | `orders.exchange` / `order.ready` |
| `delivery.retry.2` | 2 000 ms | `orders.exchange` / `order.ready` |
| `delivery.retry.3` | 4 000 ms | `orders.exchange` / `order.ready` |

Retry queue declaration arguments:

```
x-message-ttl:          <1000 | 2000 | 4000>
x-dead-letter-exchange: orders.exchange
x-dead-letter-routing-key: <original routing key>
durable:                true
```

No consumer binds to a retry queue. Messages sit in it for the TTL duration, then expire back into the primary exchange.

### 3.3 Dead-letter queues

Each primary queue has a dedicated DLQ. Messages arrive here after the consumer has nacked and not requeued a message following all retry attempts.

| DLQ | Bound to |
|---|---|
| `payment.dlq` | `payment.dlx` |
| `kitchen.dlq` | `kitchen.dlx` |
| `delivery.dlq` | `delivery.dlx` |
| `order.status.dlq` | `order.dlx` |

DLQ declaration arguments:

```
durable: true
```

No service consumes DLQs automatically. They are inspected via the RabbitMQ management UI (`:15672`) or replayed manually by re-publishing the message to `orders.exchange` with the original routing key.

---

## 4. Binding summary

```
orders.exchange  (topic)
  ├── order.created      ──► payment.queue
  ├── payment.succeeded  ──► kitchen.queue
  ├── order.ready        ──► delivery.queue
  └── (5 status keys)    ──► order.status.queue

payment.dlx  (fanout) ──► payment.dlq
kitchen.dlx  (fanout) ──► kitchen.dlq
delivery.dlx (fanout) ──► delivery.dlq
order.dlx    (fanout) ──► order.status.dlq

payment.retry.N  (x-message-ttl → orders.exchange / order.created)
kitchen.retry.N  (x-message-ttl → orders.exchange / payment.succeeded)
delivery.retry.N (x-message-ttl → orders.exchange / order.ready)
```

---

## 5. Retry and dead-letter flow

The retry mechanism is implemented entirely in broker topology — no sleep or delay logic in application code.

```
Message arrives at payment.queue
         │
         ▼
Consumer: idempotency check
         │
    ┌────┴────┐
  seen?      new?
    │          │
   ack        process
               │
          ┌────┴────┐
        ok?        fail?
          │          │
         ack        nack (requeue=false)
                     │
                     ▼
             payment.dlx routes to payment.retry.N
             (N = current attempt, read from x-death header)
                     │
               TTL expires
                     │
                     ▼
             orders.exchange / order.created
             (back to payment.queue)
                     │
             attempt < 3? ──► retry again
             attempt = 3? ──► nack → payment.dlx → payment.dlq
```

The consumer determines the current attempt number by reading the `x-death` header on the message. On the first delivery the header is absent (attempt 1). After each nack-and-expire cycle, RabbitMQ appends to the `x-death` array — the consumer reads `x-death.Count + 1` to get the current attempt number.

Retry queue selection:

```csharp
var attemptNumber = GetAttemptNumber(message); // reads x-death header
if (attemptNumber >= 3)
{
    channel.BasicNack(deliveryTag, multiple: false, requeue: false);
    // message goes to payment.dlx → payment.dlq
}
else
{
    var retryQueue = $"payment.retry.{attemptNumber}";
    channel.BasicPublish("", retryQueue, props, body);
    channel.BasicAck(deliveryTag, multiple: false);
    // ack the original, publish to retry queue with TTL
}
```

---

## 6. Message properties

All messages must be published with these properties:

| Property | Value | Reason |
|---|---|---|
| `DeliveryMode` | `2` (persistent) | Message survives broker restart |
| `ContentType` | `application/json` | Payload format |
| `MessageId` | `eventId` (UUID) | Matches `eventId` in payload — used for tracing |
| `Timestamp` | Unix epoch (seconds) | `occurredAt` expressed as AMQP timestamp |

---

## 7. Infrastructure-as-code (startup declaration)

Each service declares its own queue, retry queues, and DLQ on startup via the `RabbitMQ.Client` channel API. The exchanges are declared by every service (idempotent — RabbitMQ ignores duplicate declarations with identical arguments).

Recommended startup order within each service's `BackgroundService.StartAsync`:

1. Declare `orders.exchange` (topic, durable)
2. Declare the service's own `<service>.dlx` (fanout, durable)
3. Declare the service's primary queue with `x-dead-letter-exchange: <service>.dlx`
4. Bind the primary queue to `orders.exchange` with the service's routing key
5. Declare the service's three retry queues with appropriate TTL and DLX arguments
6. Declare the service's DLQ
7. Bind the DLQ to `<service>.dlx`
8. Begin consuming the primary queue

If any declaration fails (e.g. a queue already exists with different arguments), the service should log the error and exit — mismatched topology is a configuration bug, not a transient fault.

---

## 8. Management UI reference

The RabbitMQ management UI is available at `http://localhost:15672` (default credentials: `guest` / `guest`).

Useful views during development and chaos experiments:

| View | Path | What to check |
|---|---|---|
| Queue depths | Queues tab | Messages ready / unacked on primary queues |
| DLQ contents | Queues → `*.dlq` | Messages that exhausted retries |
| Retry queue depths | Queues → `*.retry.*` | Messages currently waiting in backoff |
| Exchange bindings | Exchanges → `orders.exchange` → Bindings | Confirm all routing keys are bound |
| Message rates | Overview | Publish / deliver / ack rates per second |

---

## Related documents

- ADR-002 — Why RabbitMQ
- ADR-003 — Why topic exchange
- ARCH-002 — Service contracts (event payloads)
- ARCH-004 — Data model (processed_event_ids table per service)
