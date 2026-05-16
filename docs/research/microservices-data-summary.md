# Microservices Data Patterns — Research Summary

Sources: Richardson, C., "Database per Service", microservices.io, https://microservices.io/patterns/data/database-per-service.html.
Newman, S., *Building Microservices*, 2nd ed., O'Reilly, 2021 — Chapter on splitting the monolith / data decomposition.

---

## Bounded context

The term comes from Domain-Driven Design (Evans, 2003). A bounded context is a boundary within which a particular domain model is valid and consistent. Inside the boundary, terms have precise meanings and the data model reflects them. Outside the boundary, the same term might mean something different or not exist at all.

In our system: "order" means something different to OrderService (a request with line items and a total) than to KitchenService (a set of items to prepare) or DeliveryService (a package to dispatch). Each service holds its own representation of the order relevant to its job. No service needs to understand the other's full model — it only needs the fields the RabbitMQ event gives it.

This is the conceptual justification for database-per-service. Each schema is the authoritative data store for one bounded context. No other service reads or writes it.

---

## Why cross-service joins are an anti-pattern

The obvious workaround when you need data from two services is to query both databases directly from one place — a shared connection, a cross-schema join, or one service reading another's tables. Richardson identifies this as the Shared Database anti-pattern.

The problems are:

- **Tight coupling.** If OrderService adds a column or renames a table, any service that queries it directly breaks. The schema becomes a shared API with no versioning, no contract, and no way to evolve independently.
- **Hidden dependencies.** There is no code or documentation that makes the dependency visible. You only discover it when a migration causes a midnight outage.
- **Ownership ambiguity.** If two services read and write the same table, neither owns it. Who runs the migrations? Who decides the schema?

Newman is direct about this: sharing a database is the most common way teams accidentally revert a microservices architecture back to a distributed monolith. The services are deployed separately but are coupled at the data layer, which is the layer that matters most.

The correct alternative is to get cross-service data through the events the services already publish. If KitchenService needs order item names, PaymentService should include them in the `payment.succeeded` event payload (or they should be in `order.created` which Kitchen already consumed). No direct database access required.

---

## Trade-offs of database-per-service

**Isolation vs reporting complexity.** Each service's schema is independent and can evolve at its own pace. This is good for development velocity and for fault isolation. The cost is that queries spanning multiple services cannot be expressed as SQL joins. A report showing all orders with their payment status and delivery status has to be assembled in application code by consuming events into a read model, or by using Change Data Capture (CDC) to pipe data into a reporting database.

**Schema independence vs distributed transactions.** With a shared database, a transaction that updates two tables is a single ACID operation — either both succeed or neither does. With database-per-service, a business operation that touches two services (e.g. creating an order and reserving payment) spans two databases. You cannot use a single SQL transaction. You have to use eventual consistency: publish an event, let the other service consume it and update its own database, and accept that there is a window where the two databases are inconsistent.

In our system this is fine. The pipeline is sequential and unidirectional — OrderService creates an order, then PaymentService processes it, then KitchenService prepares it. There is no scenario where we need an atomic update across two services simultaneously. The events are the transaction boundary.

---

## How event-driven communication makes database-per-service workable

Richardson's database-per-service page notes that the pattern creates a need for event-driven architecture. The two patterns are complementary by design: if services cannot share a database, they need another way to propagate state changes. Events are that mechanism.

In our system (ADR-001): OrderService writes to its own schema and publishes `order.created`. PaymentService consumes the event, writes to its own schema, and publishes `payment.succeeded`. No service ever queries another service's database. The event payload carries exactly the data the consumer needs. This is what makes the bounded context isolation hold at runtime — not just at design time.

The idempotency table (`processed_event_ids`) in each schema is also a consequence of this pattern. Because the only way to know what happened in another service is to consume its events, and because message queues can redeliver, each service must track which events it has already processed.

---

## Eventual consistency in reporting

The trade-off that hurts most in practice is reporting. If a user wants to see a list of all orders with their current status across all four pipeline stages, no single service has that view. Three common approaches:

**API composition.** An aggregator (the dashboard, in our case) calls each service's API and merges the results in memory. Simple to implement, adds latency proportional to the number of services.

**Event-driven read model.** A dedicated read service (or the dashboard backend) subscribes to all events and maintains a denormalised view of order state. Our `OrderStatusChanged` SignalR notification to the dashboard is exactly this pattern — each service pushes a status update and the dashboard accumulates the current state per order. The dashboard's view may lag a few milliseconds behind the most recent event but is eventually consistent.

**Change Data Capture (CDC).** Database-level change streams (e.g. Postgres logical replication, Debezium) pipe every row change into a central analytics store. More powerful but operationally heavy — out of scope for our project.

For the thesis, we use the event-driven read model. The dashboard maintains current order state by consuming `OrderStatusChanged` notifications over SignalR. This is sufficient for our experiment observation and demo purposes.
