# ARCH-002: Service Contracts — Events & Payloads

**Status:** Living document — update if event shapes change during implementation
**Last updated:** 2026-05-04

---

## 1. Overview

All inter-service communication is via RabbitMQ events on the `orders.exchange` topic exchange (see ARCH-003 for topology). This document is the authoritative definition of every event's routing key, publisher, consumers, and payload shape. No service may send or expect fields not listed here without updating this document first.

Payload serialisation: **JSON** (UTF-8). C# record types are the canonical source of truth — the JSON field names below match the default `System.Text.Json` serialisation of those records (camelCase).

---

## 2. Shared fields

Every event payload includes these fields:

| Field | Type | Description |
|---|---|---|
| `eventId` | `string` (UUID) | Unique identifier for this event instance. Used for idempotency deduplication in `processed_event_ids`. |
| `eventType` | `string` | Routing key value, duplicated in the payload for traceability (e.g. `"order.created"`). |
| `occurredAt` | `string` (ISO 8601) | UTC timestamp when the event was emitted by the publisher. |
| `orderId` | `string` (UUID) | The order this event relates to. Present on all events. |
| `outcome` | `string` | Terminal outcome of the handler step that produced this event. One of `SUCCESS`, `FAILED`, `DLQ`. Added by DOG-40. The per-event JSON examples below omit this field for brevity — assume `outcome: "SUCCESS"` for the success events and `outcome: "FAILED"` (or `"DLQ"` if `retryExhausted: true`) for failure events. |

---

## 3. Event catalogue

### 3.1 `order.created`

**Publisher:** OrderService
**Consumer:** PaymentService (queue: `payment.queue`)
**Trigger:** Client calls `POST /orders` and the order is persisted successfully.

```json
{
  "eventId": "a1b2c3d4-0000-0000-0000-000000000001",
  "eventType": "order.created",
  "occurredAt": "2026-05-04T10:00:00Z",
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "customerId": "cust-0042",
  "items": [
    { "itemId": "item-burger", "name": "Cheeseburger", "quantity": 2, "unitPriceCents": 850 },
    { "itemId": "item-fries",  "name": "Fries",        "quantity": 1, "unitPriceCents": 300 }
  ],
  "totalAmountCents": 2000,
  "currency": "USD"
}
```

| Field | Type | Description |
|---|---|---|
| `customerId` | `string` | Opaque customer identifier. |
| `items` | `OrderItem[]` | Line items in the order. At least one item required. |
| `items[].itemId` | `string` | Stable identifier for the menu item. |
| `items[].name` | `string` | Human-readable item name. |
| `items[].quantity` | `int` | Must be >= 1. |
| `items[].unitPriceCents` | `int` | Price per unit in minor currency units. |
| `totalAmountCents` | `int` | Sum of `quantity x unitPriceCents` for all items. Validated by OrderService before publishing. |
| `currency` | `string` | ISO 4217 currency code (e.g. `"USD"`). |

---

### 3.2 `payment.succeeded`

**Publisher:** PaymentService
**Consumer:** KitchenService (queue: `kitchen.queue`)
**Trigger:** PaymentService processes `order.created` and payment simulation succeeds.

```json
{
  "eventId": "a1b2c3d4-0000-0000-0000-000000000002",
  "eventType": "payment.succeeded",
  "occurredAt": "2026-05-04T10:00:01Z",
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "paymentId": "pay-0099",
  "amountChargedCents": 2000,
  "currency": "USD",
  "attemptNumber": 1
}
```

| Field | Type | Description |
|---|---|---|
| `paymentId` | `string` | Identifier for the payment record in PaymentService's schema. |
| `amountChargedCents` | `int` | Amount actually charged. Should match `totalAmountCents` from `order.created`. |
| `currency` | `string` | ISO 4217 currency code. |
| `attemptNumber` | `int` | Which retry attempt succeeded (1 = first try, 2 = second, etc.). Useful for chaos experiment metrics. |

---

### 3.3 `payment.failed`

**Publisher:** PaymentService
**Consumer:** Dashboard hub (for status display). No downstream service consumes this — the pipeline halts.
**Trigger:** PaymentService processes `order.created` and payment simulation fails after all retries are exhausted, or an unrecoverable failure is detected.

```json
{
  "eventId": "a1b2c3d4-0000-0000-0000-000000000003",
  "eventType": "payment.failed",
  "occurredAt": "2026-05-04T10:00:02Z",
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "reason": "PAYMENT_DECLINED",
  "attemptNumber": 3,
  "retryExhausted": true
}
```

| Field | Type | Description |
|---|---|---|
| `reason` | `string` | Machine-readable failure code. Values: `PAYMENT_DECLINED`, `TIMEOUT`, `CHAOS_INJECTED`. |
| `attemptNumber` | `int` | Which attempt failed. |
| `retryExhausted` | `bool` | `true` if this failure occurred after all retries were exhausted and the message will be routed to DLQ. |

---

### 3.4 `order.ready`

**Publisher:** KitchenService
**Consumer:** DeliveryService (queue: `delivery.queue`)
**Trigger:** KitchenService processes `payment.succeeded` and preparation simulation completes.

```json
{
  "eventId": "a1b2c3d4-0000-0000-0000-000000000004",
  "eventType": "order.ready",
  "occurredAt": "2026-05-04T10:00:05Z",
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "preparedAt": "2026-05-04T10:00:05Z",
  "prepDurationMs": 4200,
  "items": [
    { "itemId": "item-burger", "name": "Cheeseburger", "quantity": 2 },
    { "itemId": "item-fries",  "name": "Fries",        "quantity": 1 }
  ]
}
```

| Field | Type | Description |
|---|---|---|
| `preparedAt` | `string` (ISO 8601) | UTC timestamp when preparation completed. |
| `prepDurationMs` | `int` | Actual preparation time in milliseconds. Useful for kitchen slowdown chaos metrics. |
| `items` | `ReadyItem[]` | Items confirmed prepared. No price data needed downstream. |

---

### 3.5 `delivery.completed`

**Publisher:** DeliveryService
**Consumer:** Dashboard hub (terminal event — no downstream service).
**Trigger:** DeliveryService processes `order.ready` and delivery simulation succeeds.

```json
{
  "eventId": "a1b2c3d4-0000-0000-0000-000000000005",
  "eventType": "delivery.completed",
  "occurredAt": "2026-05-04T10:00:08Z",
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "deliveryId": "del-0017",
  "deliveredAt": "2026-05-04T10:00:08Z",
  "attemptNumber": 1
}
```

| Field | Type | Description |
|---|---|---|
| `deliveryId` | `string` | Identifier for the delivery record in DeliveryService's schema. |
| `deliveredAt` | `string` (ISO 8601) | UTC timestamp when delivery was confirmed. |
| `attemptNumber` | `int` | Which attempt succeeded. |

---

### 3.6 `delivery.failed`

**Publisher:** DeliveryService
**Consumer:** Dashboard hub (terminal failure — no downstream service).
**Trigger:** DeliveryService processes `order.ready` and delivery simulation fails after all retries, or chaos scenario 2 (delivery failure loop) is active.

```json
{
  "eventId": "a1b2c3d4-0000-0000-0000-000000000006",
  "eventType": "delivery.failed",
  "occurredAt": "2026-05-04T10:00:09Z",
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "reason": "DRIVER_UNAVAILABLE",
  "attemptNumber": 3,
  "retryExhausted": true
}
```

| Field | Type | Description |
|---|---|---|
| `reason` | `string` | Machine-readable failure code. Values: `DRIVER_UNAVAILABLE`, `ADDRESS_INVALID`, `CHAOS_INJECTED`. |
| `attemptNumber` | `int` | Which attempt failed. |
| `retryExhausted` | `bool` | `true` if the message will be routed to DLQ after this failure. |

---

## 4. OrderStatusChanged (SignalR — not a RabbitMQ event)

Each service emits an `OrderStatusChanged` notification to the SignalR hub whenever an order transitions state. This is an internal push from service to dashboard — it does not travel over RabbitMQ.

```json
{
  "orderId": "f1e2d3c4-0000-0000-0000-000000000001",
  "status": "PAYMENT_SUCCEEDED",
  "updatedAt": "2026-05-04T10:00:01Z",
  "sourceService": "PaymentService",
  "attemptNumber": 1,
  "retryExhausted": false,
  "metadata": {},
  "retryCount": 0,
  "outcome": "SUCCESS"
}
```

`retryCount` and `outcome` were added by DOG-40. `retryCount` is the number of retries that happened (`= max(0, attemptNumber - 1)`). `outcome` is one of `SUCCESS` (handler step succeeded), `FAILED` (failed but retries remain — will be retried), or `DLQ` (failed and exhausted retries — routed to DLQ). The publisher sets `outcome` on each domain event; the dashboard-api `StatusTranslator` forwards it onto the SignalR notification. `attemptNumber` and `retryExhausted` are retained for now for backward compatibility with the existing dashboard rendering; new clients should prefer `retryCount` and `outcome`.

Valid `status` values, in pipeline order:

| Status | Emitted by |
|---|---|
| `CREATED` | OrderService |
| `PAYMENT_PROCESSING` | PaymentService (on first attempt start) |
| `PAYMENT_SUCCEEDED` | PaymentService |
| `PAYMENT_FAILED` | PaymentService |
| `KITCHEN_PREPARING` | KitchenService |
| `ORDER_READY` | KitchenService |
| `DELIVERY_IN_PROGRESS` | DeliveryService |
| `DELIVERED` | DeliveryService |
| `DELIVERY_FAILED` | DeliveryService |
| `DEAD_LETTERED` | Any service (after DLQ routing) |

---

## 5. C# record definitions (canonical)

These records should live in a shared `Contracts` project referenced by all four services, or be duplicated per service if a shared project is not adopted. Do not let payload shapes diverge between publisher and consumer.

```csharp
// Shared base — all events embed these fields
public record EventBase(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId
);

public record OrderCreatedEvent(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId,
    string CustomerId,
    IReadOnlyList<OrderItem> Items,
    int    TotalAmountCents,
    string Currency
) : EventBase(EventId, EventType, OccurredAt, OrderId);

public record OrderItem(
    string ItemId,
    string Name,
    int    Quantity,
    int    UnitPriceCents
);

public record PaymentSucceededEvent(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId,
    string PaymentId,
    int    AmountChargedCents,
    string Currency,
    int    AttemptNumber
) : EventBase(EventId, EventType, OccurredAt, OrderId);

public record PaymentFailedEvent(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId,
    string Reason,
    int    AttemptNumber,
    bool   RetryExhausted
) : EventBase(EventId, EventType, OccurredAt, OrderId);

public record OrderReadyEvent(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId,
    DateTimeOffset PreparedAt,
    int    PrepDurationMs,
    IReadOnlyList<ReadyItem> Items
) : EventBase(EventId, EventType, OccurredAt, OrderId);

public record ReadyItem(string ItemId, string Name, int Quantity);

public record DeliveryCompletedEvent(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId,
    string DeliveryId,
    DateTimeOffset DeliveredAt,
    int    AttemptNumber
) : EventBase(EventId, EventType, OccurredAt, OrderId);

public record DeliveryFailedEvent(
    Guid   EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid   OrderId,
    string Reason,
    int    AttemptNumber,
    bool   RetryExhausted
) : EventBase(EventId, EventType, OccurredAt, OrderId);

public record OrderStatusChangedNotification(
    Guid   OrderId,
    string Status,
    DateTimeOffset UpdatedAt,
    string SourceService,
    int    AttemptNumber,
    bool   RetryExhausted,
    Dictionary<string, object> Metadata
);
```

---

## Related documents

- ADR-001 — Why event-driven over REST
- ADR-003 — Topic exchange and routing-key convention
- ARCH-003 — RabbitMQ topology (queues, bindings, DLX configuration)
- ARCH-004 — Data model (per-service schema tables)
