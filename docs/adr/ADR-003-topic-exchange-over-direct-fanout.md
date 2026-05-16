# ADR-003: Topic Exchange over Direct / Fanout

## Status
Accepted

## Context
RabbitMQ supports several exchange types: direct, fanout, topic, and headers. We need to choose one exchange type and topology for routing all events between our four microservices. The topology must support our current six event types (`order.created`, `payment.succeeded`, `payment.failed`, `order.ready`, `delivery.completed`, `delivery.failed`) and accommodate new event types without requiring infrastructure changes.

## Decision
We use a single topic exchange named `orders.exchange`. All services publish to this exchange. Each consumer queue is bound with a specific routing key. Routing keys follow the naming convention `<entity>.<event>` (e.g. `order.created`, `payment.succeeded`).

## Routing-key naming convention

Routing keys follow the pattern `<entity>.<event>`:

| Routing key | Publisher | Consumer queue |
|---|---|---|
| `order.created` | OrderService | `payment.queue` |
| `payment.succeeded` | PaymentService | `kitchen.queue` |
| `payment.failed` | PaymentService | _(no consumer — logged to DLQ for visibility)_ |
| `order.ready` | KitchenService | `delivery.queue` |
| `delivery.completed` | DeliveryService | _(terminal — consumed by dashboard hub)_ |
| `delivery.failed` | DeliveryService | _(terminal — consumed by dashboard hub)_ |

The `<entity>` segment is the domain noun in lowercase singular (`order`, `payment`, `delivery`). The `<event>` segment is a past-tense verb phrase in lowercase (`created`, `succeeded`, `failed`, `ready`, `completed`). No other separators or casing are used.

New event types follow the same pattern without any change to the exchange configuration — only a new binding is required on the consuming queue.

## Rationale

**Routing-key flexibility without re-binding.** A topic exchange routes messages to queues based on pattern matching against the routing key. Bindings use `*` (one word) and `#` (zero or more words) wildcards. This means a consumer can subscribe to `payment.*` to receive both `payment.succeeded` and `payment.failed` with a single binding, or subscribe to `#` to receive every event on the exchange. Direct exchanges require an exact key match per binding — adding a new event type (`payment.refunded`) would require a new binding on every interested queue. Fanout ignores routing keys entirely, so every queue receives every message regardless of type, pushing filtering logic into application code.

**Single exchange simplifies topology.** One exchange for all events means one place to inspect message flow in the RabbitMQ management UI. Publishers need one connection target. Bindings are the only per-event configuration. Compared to a direct exchange topology with one exchange per event type, or a fanout topology per service pair, a single topic exchange is easier to reason about and easier to document for the thesis.

**Future event types at zero infrastructure cost.** Adding a new event (e.g. `order.cancelled`, `kitchen.rejected`) requires publishing with the new routing key and adding a binding on the consuming queue. The exchange itself, and all existing bindings, are unchanged. With a direct exchange, every new event type is a schema-level change to the exchange topology. With fanout, there is no routing key to add — but filtering must be implemented in each consumer, spreading topology knowledge into application code.

**Consistent with EIP vocabulary.** Hohpe & Woolf (2003) identify the Publish-Subscribe Channel and Message Filter as foundational patterns. A topic exchange is a native implementation of both: the exchange is the channel, and routing key bindings are the filter. This alignment makes the thesis argument straightforward — the infrastructure directly instantiates the patterns being studied.

## Trade-offs

**Slightly more configuration than fanout.** A fanout exchange requires no routing keys and no per-queue bindings — every queue bound to the exchange receives every message. Our topic exchange requires a binding declaration per consumer queue specifying the routing key. In practice this is a small amount of one-time setup code in each service's startup, but it is non-zero overhead compared to fanout.

**Wildcard bindings can cause accidental over-subscription.** If a consumer binds with `#` or `order.*` without careful intention, it will receive messages it does not expect. A direct exchange makes over-subscription impossible by design. We mitigate this by binding each queue to an explicit, fully-qualified routing key (no wildcards in production bindings) and documenting the convention in this ADR.

**All events share one exchange — a misconfigured publisher can affect unrelated consumers.** If a service publishes to the wrong routing key, the message will be silently dropped (no matching binding) or routed to an unintended queue. We mitigate this with the naming convention above and integration tests on the happy-path flow (DOG-36).

## Alternatives rejected

**Direct exchange** — requires an exact routing key match per binding. Adding a new event type requires updating bindings on all interested queues. Provides no routing flexibility benefit over topic exchange for our event set.

**Fanout exchange** — ignores routing keys; every bound queue receives every message. All event-type filtering must be implemented in application code, spreading topology knowledge away from the broker and into each consumer. Unsuitable for a system where consumers care about specific event types.

**One exchange per service pair** — e.g. `order-payment.exchange`, `payment-kitchen.exchange`. Increases the number of exchanges to configure and monitor, makes the management UI harder to read, and couples the exchange topology to the current service graph rather than to the event vocabulary.

## References
- Hohpe & Woolf, *Enterprise Integration Patterns*, 2003 — Publish-Subscribe Channel, Message Filter
- RabbitMQ documentation, *Exchanges*, https://www.rabbitmq.com/docs/exchanges
