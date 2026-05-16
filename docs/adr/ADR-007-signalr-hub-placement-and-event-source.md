# ADR-007: SignalR Hub Placement and Status-Event Source

## Status
Accepted

## Context

ARCH-002 § 4 specifies an `OrderStatusChanged` notification that the dashboard
displays in real time. ARCH-001 reserves a SignalR hub on the dashboard's port
5000. Two questions need answering before that hub can be built:

1. **Where does the hub run?** Co-located with OrderService, hosted in a
   dedicated dashboard-api service, or embedded in the frontend application
   itself (only viable if the frontend is Blazor Server).
2. **How do the four pipeline services notify the hub?** Direct HTTP calls
   from each service to the hub, each service holding a long-lived SignalR
   client connection to the hub, or the hub-side service subscribing to the
   message bus and translating events into notifications.

The four pipeline services already publish every state transition to
`orders.exchange` as a domain event (`order.created`, `payment.succeeded`,
`order.ready`, `delivery.completed`, plus their failure variants). The
information the dashboard needs is **already on the bus**.

ADR-001 establishes event-driven messaging over RabbitMQ as the inter-service
communication mechanism and rejects direct HTTP between services. That
constraint frames the second decision.

## Decision

1. **A separate `dashboard-api` ASP.NET Core service hosts the SignalR hub** at
   `/hubs/orders`. It is the only service that exposes a SignalR endpoint and
   the only service the frontend connects to for live updates.
2. **`dashboard-api` subscribes to the RabbitMQ bus** as another consumer
   (queue: `dashboard.queue`, bound to `orders.exchange` for every status-
   bearing routing key). Each inbound event is translated into an
   `OrderStatusChangedNotification` and pushed to every connected SignalR
   client via `IHubContext<OrdersHub>`.

The four pipeline services are **not modified**. They continue to publish
their existing domain events; the dashboard interest is invisible to them.

## Rationale

**The bus-subscriber model preserves ADR-001.** No service needs to know the
dashboard's address, its protocol, or whether it is even running. The
dashboard fans in from the bus the same way every other consumer does — same
exchange, same `processed_event_ids` pattern if we ever need it. The
event-driven principle survives end-to-end.

**A dedicated dashboard-api service gives the hub a clear owner.** SignalR
state (connection registry, group memberships, scale-out backplane if added
later) is dashboard-specific. Co-locating it with OrderService would mean
OrderService scales as a function of *dashboard load*, not order load —
backwards. A dedicated service can scale, deploy, and be operated independently.

**The hub is independent of the frontend choice.** ADR-006 has not yet locked
the frontend technology (React vs Blazor). Embedding the hub in the frontend
would couple this decision to that one; a dedicated dashboard-api works for
both. The same hub serves a React SignalR client and a Blazor Server hub
proxy identically.

**Latency is acceptable.** Bus-routed status updates land in the dashboard
within tens of milliseconds end-to-end on a healthy broker — well under the
threshold where a human notices a delay. Real-time-feel is preserved without
introducing direct service-to-service calls.

**Dashboard outages don't stop the pipeline.** If dashboard-api crashes, the
queue accumulates and the pipeline keeps running. When dashboard-api restarts,
it drains the queue and catches up. No producer ever waits on the dashboard.

**Translation is centralised.** Mapping `order.created` →
`Status = "CREATED", SourceService = "OrderService"` lives in a single
`StatusTranslator` class in dashboard-api. Adding a new event type or a new
status string changes one file. A direct-HTTP design would require every
producing service to know the dashboard's wire format.

## Trade-offs

**Extra container in the system.** One more service to operate, configure,
and run in docker-compose. Acceptable given the alternative is a tangle of
direct connections from every service to a co-located hub.

**Status update is one hop slower than direct HTTP.** A direct HTTP call
from PaymentService to the hub would skip the broker round-trip — tens of
milliseconds saved. For a 200–700 ms simulated pipeline this is invisible to
the user. For the thesis we explicitly value the architectural cleanliness
over the latency saving.

**No backpressure path back to producers.** If dashboard-api is overwhelmed
(unlikely at thesis scale), it can't tell producers to slow down — they just
keep publishing. This is the same behaviour as every other consumer in the
system, so the trade-off is consistent.

**`PAYMENT_PROCESSING`, `KITCHEN_PREPARING`, `DELIVERY_IN_PROGRESS`
intermediate statuses are skipped in M1.** ARCH-002 § 4 lists these
"started" statuses but the bus only carries completion events. With the
M1 simulators running in hundreds of milliseconds, these states are
transient anyway. If the dashboard wants to render an "in-flight" animation
later, the right fix is to publish explicit "started" events to the bus —
out of scope for now.

## Alternatives rejected

**Co-locate hub with OrderService.** Rejected. Mixes two unrelated
responsibilities (accept new orders, broadcast UI updates), couples
OrderService scaling to dashboard load, and makes downstream services
(PaymentService, KitchenService, DeliveryService) need a way to reach
OrderService — either HTTP (violates ADR-001) or another bus binding (same
amount of plumbing as the chosen design but in the wrong place).

**Each service makes an HTTP POST to the hub on every status transition.**
Rejected. Violates ADR-001 ("no direct HTTP between services"). Introduces
a new failure mode: what does PaymentService do if the dashboard is down?
Either it blocks (terrible) or fires-and-forgets (loses status updates).
Requires every service to know the dashboard's address and contract. Adds
producer-side complexity for a consumer-side concern.

**Each service holds a long-lived SignalR client connection to the hub.**
Rejected. SignalR clients are typically browsers, not services. Connection
management (reconnect, backoff, scale-out) is non-trivial. Same coupling
problem as the HTTP variant: producers know about the dashboard.

**Embed the hub in a Blazor Server frontend.** Rejected. Locks the frontend
to Blazor Server before ADR-006 has chosen, and merges UI and integration
concerns in one process.

## Implementation notes

**Topology:** `dashboard.queue` is a durable, non-exclusive queue bound to
`orders.exchange` for every status-bearing routing key (`order.created`,
`payment.succeeded`, `payment.failed`, `order.ready`, `delivery.completed`,
`delivery.failed`). No retry queues, no DLQ — a failed broadcast is logged
and acked. There is nothing recoverable about a missed UI update; the next
event will refresh the dashboard's view of that order anyway.

**Idempotency:** No `processed_event_ids` table. The dashboard has no
database. A duplicate broadcast (from a redelivered message) results in the
client briefly seeing the same status twice; status is monotonic per order
so the UI's display logic is naturally idempotent.

**Broadcaster abstraction:** Handlers depend on `IOrderStatusBroadcaster`,
not on `IHubContext<OrdersHub>` directly. The SignalR implementation
(`SignalROrderStatusBroadcaster`) wraps `IHubContext`. Tests inject a
`FakeOrderStatusBroadcaster` and assert against the captured notifications.

## References
- ARCH-001 — System overview (mentions dashboard SignalR port 5000)
- ARCH-002 § 4 — `OrderStatusChangedNotification` shape and status enum
- ADR-001 — Event-driven over REST
- ADR-006 — Frontend framework decision (deferred; this ADR is independent of it)
