# System walkthrough (M1)

A compact map of what currently exists: all four pipeline services, the
dashboard-api SignalR fan-in, and the shared building blocks they're built
on top of. Read this end-to-end and you should be able to trace a request
through the whole running pipeline and know where to look for any given
piece of behaviour.

---

## 1. What's running today

```
                                                ┌────────────────────────┐
  client                       browser ◀───────│   dashboard-api        │
     │                         (SignalR)        │   /hubs/orders         │
     │ POST /orders                             │   StatusEventsConsumer │
     ▼                                          └──────────▲─────────────┘
  ┌───────────────┐                                        │ binds all
  │ OrderService  │                                        │ status keys
  │  (HTTP API)   │                                        │
  └───────┬───────┘                                        │
          │  order.created                                 │
          ▼                                                │
  ┌──────────────────┐                                     │
  │  PaymentService  │                                     │
  └──────────┬───────┘                                     │
             │  payment.succeeded                          │
             ▼                                             │
  ┌──────────────────┐         orders.exchange (topic)─────┘
  │  KitchenService  │
  └──────────┬───────┘
             │  order.ready
             ▼
  ┌──────────────────┐
  │ DeliveryService  │
  └──────────┬───────┘
             │  delivery.completed
             ▼
        (terminal)
```

**OrderService** ([services/order](../services/order)) exposes one HTTP
endpoint, persists the order, and publishes `order.created`.
**PaymentService** ([services/payment](../services/payment)) consumes
`order.created`, simulates a payment (M1: always succeeds), persists a
`payment_record`, and publishes `payment.succeeded`.
**KitchenService** ([services/kitchen](../services/kitchen)) consumes
`payment.succeeded`, simulates prep (200–500 ms), persists a `kitchen_order`,
and publishes `order.ready`.
**DeliveryService** ([services/delivery](../services/delivery)) consumes
`order.ready`, simulates dispatch (300–700 ms, M1: always succeeds),
persists a `delivery`, and publishes `delivery.completed` — the terminal
event of the pipeline.
**dashboard-api** ([services/dashboard-api](../services/dashboard-api))
fans the bus into a SignalR hub. It subscribes to every status-bearing
routing key on `orders.exchange`, translates each event into an
`OrderStatusChangedNotification`, and broadcasts to every connected browser
via the `/hubs/orders` hub. See [ADR-007](adr/ADR-007-signalr-hub-placement-and-event-source.md)
for the design rationale.

All inter-service traffic is via **`orders.exchange`** (topic, durable) on
RabbitMQ. Each pipeline service has its own Postgres schema (`orders`,
`payments`, `kitchen`, `delivery`). dashboard-api has no database — it
holds SignalR connection state in memory.

---

## 2. Repository map

```
shared/
  Reservoir.BuildingBlocks/             ← shared by every service
    Messaging/                           IEventPublisher, RabbitMqEventPublisher,
                                         RabbitMqOptions, DI extensions
    Contracts/                           All 6 event records (order.created,
                                         payment.{succeeded,failed},
                                         order.ready, delivery.{completed,failed})
                                         + OrderStatus enum + OrderStatusChangedNotification
                                         + RoutingKeys string constants
    Persistence/                         ProcessedEventId POCO (idempotency)
    Serialization/                       EventJsonOptions.Web

  Reservoir.TestSupport/                ← shared by every *.Tests project
    FakeEventPublisher                   captures publishes for assertions
    FakeTimeProvider                     freezable clock
    BrokerProbe                          "is RabbitMQ reachable?" probe
    RabbitMqVerifierQueue                exclusive queue + reader for asserts

services/order/                         OrderService.csproj
  Api/                                   HTTP request/response DTOs
  Data/                                  EF DbContext + Order/OrderItem entities
  Domain/                                OrderStatus constants
  Handlers/CreateOrderHandler.cs         the use case
  Program.cs                             host + DI + POST /orders

services/payment/                       PaymentService.csproj
  Consumer/                              PaymentConsumer (BackgroundService) +
                                         PaymentConsumerOptions
  Data/                                  EF DbContext + PaymentRecord
  Domain/                                PaymentStatus + failure reasons
  Handlers/OrderCreatedHandler.cs        the use case
  Simulation/                            IPaymentSimulator + impl + options
  Program.cs                             host + DI + /health

services/kitchen/                       KitchenService.csproj
  Consumer/                              KitchenConsumer (BackgroundService) +
                                         KitchenConsumerOptions
  Data/                                  EF DbContext + KitchenOrder
  Domain/                                KitchenOrderStatus (PREPARING/READY)
  Handlers/PaymentSucceededHandler.cs    the use case
  Simulation/                            IKitchenSimulator + impl + options
  Program.cs                             host + DI + /health

services/delivery/                      DeliveryService.csproj
  Consumer/                              DeliveryConsumer (BackgroundService) +
                                         DeliveryConsumerOptions
  Data/                                  EF DbContext + Delivery
  Domain/                                DeliveryStatus + failure reasons
  Handlers/OrderReadyHandler.cs          the use case
  Simulation/                            IDeliverySimulator + impl + options
  Program.cs                             host + DI + /health

services/dashboard-api/                 DashboardApi.csproj  (no DB)
  Hubs/                                  OrdersHub (SignalR), IOrderStatusBroadcaster,
                                         SignalROrderStatusBroadcaster
  Consumer/                              StatusEventsConsumer (BackgroundService) +
                                         DashboardConsumerOptions
  Handlers/StatusTranslator.cs           routing-key → notification mapping
  Program.cs                             host + DI + /hubs/orders + CORS

services/order.Tests/                   xUnit, mocks publisher + EF InMemory
services/payment.Tests/                 xUnit, same pattern + integration host
services/kitchen.Tests/                 xUnit, same pattern + integration host
services/delivery.Tests/                xUnit, same pattern + integration host
services/dashboard-api.Tests/           xUnit, translator unit + bus→broadcaster integration
```

---

## 3. Tracing one request end-to-end

This is the path of a single `POST /orders` through the running system.

### 3.1 `OrderService.Program.cs`

DI registrations (one screen of code). The interesting calls:

```csharp
services.AddRabbitMqPublisher(builder.Configuration);
services.PostConfigure<RabbitMqOptions>(o => o.PublisherClientName = "order-service-publisher");
services.AddScoped<CreateOrderHandler>();
app.MapPost("/orders", async (CreateOrderRequest r, CreateOrderHandler h, ...) => ...);
```

`AddRabbitMqPublisher` is a one-line extension on the shared
`Reservoir.BuildingBlocks.Messaging` namespace — it binds
`RabbitMqOptions` from config and registers
`RabbitMqEventPublisher` as the singleton `IEventPublisher`. Same call will
appear in every service.

### 3.2 `CreateOrderHandler.HandleAsync`

The use case:

1. Validate the request (customerId + ≥1 item + sane quantities).
2. Compute `totalAmountCents` server-side (never trust the client's total).
3. Generate `orderId` and `eventId` (UUIDs).
4. Build the `Order` aggregate in memory, status = `CREATED`.
5. Open a DB transaction → add the order → `SaveChangesAsync`.
6. Build the `OrderCreatedEvent` payload (from
   `Reservoir.BuildingBlocks.Contracts`).
7. Publish via `_publisher.Publish("order.created", evt, eventId, now)`.
8. Commit. Return `201 Created` with `orderId` and `eventId`.

The `eventId` is the **idempotency key** that flows into the payload and is
also set as the AMQP `MessageId`. Downstream consumers check it against
`processed_event_ids` to deduplicate redeliveries.

### 3.3 `RabbitMqEventPublisher` (shared)

[shared/Reservoir.BuildingBlocks/Messaging/RabbitMqEventPublisher.cs](../shared/Reservoir.BuildingBlocks/Messaging/RabbitMqEventPublisher.cs):

- Singleton. One connection, one channel, kept open for the lifetime of the app.
- Declares `orders.exchange` (topic, durable) and `orders.dlx` (fanout,
  durable) on startup — both calls are idempotent so every service can
  declare them safely.
- `ConfirmSelect()` enables publisher confirms; every publish waits for a
  broker ack (`WaitForConfirmsOrDie`). Without this you'd never know whether
  the broker actually persisted the message.
- Sets `DeliveryMode=2` (persistent), `MessageId=eventId`, `Type=routingKey`,
  `ContentType=application/json`. Channel is not thread-safe so publishes
  are serialised through an internal lock.

### 3.4 `PaymentConsumer` (PaymentService)

A `BackgroundService` that runs for the lifetime of the host:

1. On `ExecuteAsync`:
   - Open connection (`DispatchConsumersAsync = true` for async handlers).
   - Declare the same two exchanges.
   - Declare `payment.queue` with `x-dead-letter-exchange: orders.dlx`.
   - Bind `payment.queue` ← `orders.exchange` / `order.created`.
   - Declare `payment.retry.{1,2,3}` (TTL-based retry queues, per ARCH-003;
     unused in M1 but declared so the topology is complete).
   - Declare `payment.dlq`, bound to `orders.dlx`.
   - `BasicConsume` with an `AsyncEventingBasicConsumer`.
2. On every message:
   - Open a DI scope → resolve `OrderCreatedHandler` → call `HandleAsync`.
   - On success: `BasicAck`.
   - On exception: `BasicNack(requeue: false)` → goes to `orders.dlx` →
     `payment.dlq`.

### 3.5 `OrderCreatedHandler.HandleAsync`

The consumer-side use case:

1. Deserialize `OrderCreatedEvent` (shared contract — same bytes the
   publisher emitted).
2. Read `attemptNumber` from the AMQP `x-death` header (1 on first delivery).
3. Idempotency check: query `payments.processed_event_ids` for `eventId`.
   If found, log + return (consumer will ack).
4. Call `IPaymentSimulator.SimulateAsync(evt)` — random delay 100–300 ms,
   `SuccessProbability = 1.0` for M1.
5. Open a DB transaction.
6. Insert `PaymentRecord` (status = `SUCCEEDED` for M1; `FAILED` path exists
   for later).
7. Insert `ProcessedEventId` (closes the idempotency loop).
8. `SaveChangesAsync`.
9. Build `PaymentSucceededEvent` (shared contract), publish to
   `payment.succeeded` routing key.
10. Commit. Return.

The publish happens **inside** the transaction so a publish failure rolls
back the payment record — same trade-off as the order service. Production
fix is the transactional outbox pattern; out of scope for M1.

### 3.6 `KitchenConsumer` (KitchenService)

Structurally identical to `PaymentConsumer`. Differences:

- Binds `kitchen.queue` ← `orders.exchange` / `payment.succeeded`.
- DLQ is `kitchen.dlq`, retry queues are `kitchen.retry.{1,2,3}`.
- Dispatches to `PaymentSucceededHandler` instead of `OrderCreatedHandler`.

Topology declared on startup, message loop ack/nacks the same way.

### 3.7 `PaymentSucceededHandler.HandleAsync`

The consumer-side use case for the kitchen:

1. Deserialize `PaymentSucceededEvent` (shared contract).
2. Idempotency check against `kitchen.processed_event_ids`. If seen, ack
   and return.
3. Record `startedAt = now`, then run `IKitchenSimulator.SimulateAsync(evt)`
   (200–500 ms `Task.Delay`). Record `readyAt = now`. The simulate happens
   **outside** the DB transaction so a pooled connection isn't held open
   across the sleep.
4. Open DB transaction.
5. Insert `KitchenOrder` row: `status = READY`, both `prep_started_at` and
   `prep_ready_at` set, `prep_duration_ms = readyAt − startedAt`.
6. Insert `ProcessedEventId` for the inbound event.
7. `SaveChangesAsync`.
8. Build `OrderReadyEvent` (shared contract). **M1 limitation**: `items` is
   emitted as an empty list — see § 7.
9. Publish to `order.ready` routing key.
10. Commit. Return.

### 3.8 `DeliveryConsumer` (DeliveryService)

Structurally identical to PaymentConsumer / KitchenConsumer. Differences:

- Binds `delivery.queue` ← `orders.exchange` / `order.ready`.
- DLQ is `delivery.dlq`, retry queues are `delivery.retry.{1,2,3}`.
- Dispatches to `OrderReadyHandler` instead of the previous handlers.

### 3.9 `OrderReadyHandler.HandleAsync`

The terminal use case in the pipeline:

1. Deserialize `OrderReadyEvent` (shared contract).
2. Read `attemptNumber` from the AMQP `x-death` header (1 on first delivery).
3. Idempotency check against `delivery.processed_event_ids`.
4. Record `startedAt = now`, then run `IDeliverySimulator.SimulateAsync(evt)`
   (300–700 ms `Task.Delay`, M1: always succeeds). Record `completedAt = now`.
5. Open DB transaction.
6. Insert `Delivery` row: `status = COMPLETED` (or `FAILED`), `attempt_number`,
   `started_at`, `completed_at`, optional `failure_reason`. Multiple rows per
   order are possible (per ARCH-004 § 5) when chaos scenario 2 triggers retries.
7. Insert `ProcessedEventId`.
8. `SaveChangesAsync`.
9. Build `DeliveryCompletedEvent` (or `DeliveryFailedEvent` on the failure
   path). Publish to `delivery.completed` (or `delivery.failed`).
10. Commit.

This is the **terminal** event of the pipeline as far as the four pipeline
services are concerned — no service consumes `delivery.completed`. The
dashboard does (see § 3.10).

### 3.10 `dashboard-api` fan-in (parallel to the pipeline)

While the four pipeline services are passing the order down the line,
**dashboard-api is also bound** to `orders.exchange` and receives every
status-bearing event the moment it's published. The flow per event:

1. `StatusEventsConsumer` (BackgroundService) is bound to `dashboard.queue`,
   which has bindings for `order.created`, `payment.succeeded`,
   `payment.failed`, `order.ready`, `delivery.completed`, and
   `delivery.failed` — all on `orders.exchange`.
2. On every message: `StatusTranslator.Translate(routingKey, body)` returns
   an `OrderStatusChangedNotification` with `Status` mapped per ARCH-002 § 4
   (e.g. `payment.succeeded` → `Status = "PAYMENT_SUCCEEDED"`,
   `SourceService = "PaymentService"`).
3. `IOrderStatusBroadcaster.BroadcastAsync(notification)` pushes via
   `IHubContext<OrdersHub>` to all connected SignalR clients on
   `/hubs/orders` using the `OrderStatusChanged` method name.
4. The consumer acks regardless of broadcast outcome — see ADR-007 for why
   broadcast failures are non-recoverable.

The pipeline services know nothing about this. They publish their domain
events; dashboard-api fans the bus into the browser as a passive observer.

---

## 4. Shared building blocks — what each piece earns its place doing

### `Reservoir.BuildingBlocks.Messaging`

| Type | Why it's shared |
|---|---|
| `IEventPublisher` | One narrow interface, used by every service that publishes. Lets handlers be unit-tested with `FakeEventPublisher`. |
| `RabbitMqEventPublisher` | ~95 lines of broker wiring (connection, channel, exchange declares, publisher confirms, message-property setup). Identical across services — would be copy-pasted 4× otherwise. |
| `RabbitMqOptions` | Just the connection + exchange names. Consumer-specific knobs (queue name, prefetch, routing key to subscribe to) stay per-service as `<Service>ConsumerOptions`. |
| `AddRabbitMqPublisher()` | DI extension. Means each service's `Program.cs` adds the publisher in a single line. |

### `Reservoir.BuildingBlocks.Contracts`

Six `record` types matching ARCH-002 § 5, plus `RoutingKeys` (string
constants for routing keys), `OrderStatus` (the canonical pipeline-status
enum), and `OrderStatusChangedNotification` (the SignalR payload the
dashboard pushes to browsers).

**The single source of truth** for every event and notification the system
emits or consumes. If we updated a payload shape on the producer side and
forgot the consumer, we'd get a runtime JSON deserialisation failure;
sharing the records means the compiler catches it across every service at
build time.

ARCH-002 explicitly flagged the "duplicate or share" trade-off; we picked
share because the rule of three was about to bite (4 services × N events).

### `Reservoir.BuildingBlocks.Persistence.ProcessedEventId`

A two-property POCO (EventId + ProcessedAt). Each consumer service maps it
in its own `DbContext` (different schema, same shape). The EF mapping stays
per-service because schema/column-naming is per-service.

### `Reservoir.TestSupport`

| Type | Used by |
|---|---|
| `FakeEventPublisher` | every handler unit test |
| `FakeTimeProvider` | every handler unit test (freezable clock) |
| `BrokerProbe.IsAvailable(opts)` | every integration test (skips when broker is down) |
| `RabbitMqVerifierQueue.Open(opts, key)` | every integration test (the queue you bind to capture what the service emitted) |

The integration-test setup boilerplate is non-trivial; sharing it means each
service's integration test stays under ~150 lines and focused on the
assertion, not the plumbing.

---

## 5. Testing strategy

Each service has the same two test tiers:

**Unit tests** (handler in isolation):
- Real `DbContext` against EF Core's **InMemory provider**.
- `FakeEventPublisher` captures publishes for assertion.
- `FakeTimeProvider` pins "now" so timestamp assertions are exact.
- For PaymentService: `FakePaymentSimulator` returns a configured outcome
  instantly (no actual delay).
- For KitchenService: `FakeKitchenSimulator` returns a configured prep
  duration instantly.
- For DeliveryService: `FakeDeliverySimulator` returns a configured outcome
  (success/fail + reason) instantly.
- For dashboard-api: no simulator (no business logic to simulate) — the
  translator's mapping is verified per routing key, and a
  `FakeOrderStatusBroadcaster` captures broadcasts for assertions.
- Fast: ~16 s for all five suites combined.

**Integration tests** (in-process host + real broker):
- `BrokerProbe.IsAvailable(...)` probes localhost:5672 with a 2 s timeout.
  If the broker isn't there → `[SkippableFact]` skips the test, suite stays
  green on a fresh checkout.
- `RabbitMqVerifierQueue.Open(...)` opens an exclusive queue bound to the
  routing key under test (e.g. `payment.succeeded`, `order.ready`,
  `delivery.completed`).
- For Payment / Kitchen / Delivery: spins up a real `IHost` with the real
  consumer registered, publishes the inbound event directly to the broker,
  asserts the outbound event lands on the verifier queue, and inspects the
  in-memory persistence row.
- For dashboard-api: same in-process host pattern but with a fake
  broadcaster injected — publishes 4 inbound events (one per pipeline
  stage) and asserts the broadcaster receives 4 correctly-translated
  notifications.
- To actually run: `docker compose up -d && dotnet test`.

Current totals: **33 unit tests passing**, **5 integration tests** that
auto-skip until you start Docker.

---

## 6. What's next

The M1 pipeline is **complete**: every event from ARCH-002 flows
end-to-end, every queue from ARCH-003 is declared, every table from
ARCH-004 (except `metrics.events`) has at least one writer, and live
status updates fan out over SignalR to anyone who connects to
`/hubs/orders`. The shared layer carries the bits that would otherwise
differ between services for the wrong reason; service folders carry the
bits that *should* differ.

Upcoming work (per the milestones in ARCH-001 / ADR-005):

| Area | What it adds |
|---|---|
| **Dashboard frontend** (DOG-21) | React or Blazor app that calls `POST /orders` and subscribes to `/hubs/orders` on dashboard-api to render live state. The hub is ready; only the frontend client is missing. |
| **Retry/DLQ activation** | Today exceptions go straight to DLQ via nack. The `retry.{1,2,3}` queues exist but consumers don't republish to them. Chaos work will wire that loop using the `x-death` header pattern. |
| **Chaos engine** (ARCH-005) | A runtime knob that injects 5 failure scenarios — payment declines, delivery loops, network partitions, broker restarts, slow kitchens. Per-service simulator options already expose `SuccessProbability` / delay ranges, so most of the surface is in place. |
| **Outbox pattern** | Replace "publish inside transaction" with an outbox table + worker. Production-grade durability; current trade-off is documented in § 3 and § 7. |
| **`metrics.events`** (ARCH-004 § 6) | Append-only schema each service writes to for experiment data. Needed before experiments can be measured. |
| **`order.ready` items propagation** | See § 7 below. Subscribing Kitchen to `order.created` is the cleanest fix. |
| **In-flight statuses** (`PAYMENT_PROCESSING`, `KITCHEN_PREPARING`, `DELIVERY_IN_PROGRESS`) | ARCH-002 § 4 lists these but the bus only carries completion events. Adding explicit "started" events to the bus would let dashboard-api emit them too. |

---

## 7. Known limitations (intentional for M1)

- **Retry queues are declared but unused.** ARCH-003's
  `<service>.retry.{1,2,3}` queues are created, but the consumer doesn't
  republish failed messages into them — exceptions go straight to DLQ via
  nack. Chaos-engineering tasks will wire the republish logic.
- **No outbox pattern.** Publish + DB commit are inside one transaction; if
  the publish succeeds but the commit fails the consumer sees a phantom
  event. Acceptable for thesis scope; the outbox is a documented future
  improvement.
- **PaymentSimulator and DeliverySimulator always succeed** in M1
  (`SuccessProbability = 1.0` in both `appsettings.json` files). The failure
  paths (`payment.failed`, `delivery.failed`) are implemented and unit-tested
  but never fire at runtime until chaos work flips the probabilities.
- **`order.ready` is emitted with an empty `items` list.**
  `PaymentSucceededEvent` deliberately doesn't carry items (ARCH-002 § 3.2),
  so the kitchen has nothing to forward. Downstream nothing depends on items
  in M1 (DeliveryService doesn't use them; dashboard isn't built), so this
  is observable but not functional. Proper fix: subscribe KitchenService to
  `order.created` as well to learn items, or forward items through
  `payment.succeeded`.
- **.NET 9 runtime not installed locally** — test projects use
  `<RollForward>Major</RollForward>` so they run on the .NET 10 runtime.
  No effect on the service hosts themselves.
