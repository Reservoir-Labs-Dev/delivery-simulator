# ADR-004: Database-per-Service over Shared Database

## Status
Accepted

## Context
Our four microservices each need to persist state: orders, payment records, kitchen preparation state, and delivery records. We must decide whether to give each service its own isolated database (or schema), or to share a single database across all services. This decision affects service independence, deployment complexity, and how clearly our fault-tolerance thesis argument can be made.

## Decision
Each service owns its own PostgreSQL schema within a single PostgreSQL instance. No service reads or writes another service's schema directly. Cross-service state is communicated exclusively via RabbitMQ events.

The schema assignment is:

| Service | Schema |
|---|---|
| OrderService | `orders` |
| PaymentService | `payments` |
| KitchenService | `kitchen` |
| DeliveryService | `delivery` |

## Rationale

**Service independence is the thesis argument.** The system is designed to demonstrate that services can fail, recover, and process events independently without cascading failures. A shared database undermines this directly: a schema migration, a long-running transaction, or a connection pool exhaustion in one service's tables can block or corrupt another service's operations. Separate schemas enforce the boundary at the infrastructure level, not just by convention.

**Idempotency requires per-service deduplication state.** Each service maintains a `processed_event_ids` table to deduplicate redelivered messages. This table must be local to the service — it tracks which events that service has processed, and no other service has any business reading or writing it. A shared database makes this boundary harder to enforce and easier to accidentally violate.

**Independent schema evolution.** Each service can add columns, rename tables, or run migrations without coordinating with other services. With a shared database, any migration touches a shared resource and risks breaking other services' queries or ORM mappings. For a two-person project working on multiple services in parallel, this isolation reduces merge conflicts and deployment risk.

**Practical compromise: one Postgres instance, separate schemas.** True database-per-service at production scale means a separate database server per service. For a local Docker Compose environment, running four Postgres containers adds resource overhead and operational complexity with no educational benefit. A single Postgres instance with one schema per service preserves all the logical isolation properties — no cross-schema foreign keys, no shared tables, independent migration histories — while keeping the Docker Compose setup simple. This is the approach recommended by Richardson (microservices.io) for development and thesis-scale deployments [1].

**EF Core supports schema-scoped DbContexts.** Each service defines its own `DbContext` with a `defaultSchema` set to its schema name. Migrations are generated and applied per service. There is no shared migration history and no shared `DbContext`. This is a straightforward EF Core pattern with good tooling support.

## Schema layout (per service)

Each service's schema contains at minimum:

```
<schema>/
  orders | payments | kitchen_orders | deliveries   -- primary domain table
  processed_event_ids                               -- idempotency deduplication
  outbox (optional, if transactional outbox pattern is adopted)
```

The `processed_event_ids` table has the same structure in every service:

```sql
CREATE TABLE processed_event_ids (
    event_id   UUID        PRIMARY KEY,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

## Trade-offs

**Cross-service queries are not possible.** If a future feature requires joining order data with payment data (e.g. a reporting view), it cannot be expressed as a SQL query. The data must be assembled in application code by consuming events or by calling service APIs. For our current scope this is not a requirement, but it is a real constraint that limits ad-hoc querying and reporting.

**Eventual consistency between schemas.** Each service's schema reflects the world as it knew it at the time it processed its last event. At any given moment, the `orders` schema may show an order as `CREATED` while the `payments` schema already shows it as `PAID`. This is an inherent property of event-driven systems and is explicitly discussed in our thesis under eventual consistency (CAP theorem, Gilbert & Lynch, 2002). It is a trade-off we accept and examine, not a defect to be fixed.

**Migration coordination at startup.** Each service runs EF Core migrations on startup (`Database.MigrateAsync()`). In Docker Compose, all four services start roughly simultaneously after Postgres becomes healthy. If two services attempt to create their schemas at exactly the same time, Postgres handles this safely — schemas are independent and there are no shared migration tables. No coordination mechanism is required beyond the `depends_on: postgres` health check.

**Single Postgres instance is a shared failure point.** If the Postgres container crashes, all four services lose their databases simultaneously. In production, true database-per-service would isolate this. For our purposes, Postgres availability is treated as infrastructure-level reliability (outside the scope of our chaos scenarios), and our chaos engine targets application-layer failures instead.

## Alternatives rejected

**Shared database, shared schema** — all services read and write the same tables. Maximally simple to set up but eliminates service independence entirely. A slow query or bad migration in any service affects all others. Incompatible with the fault-isolation thesis argument.

**Shared database, shared schema with table-level ownership by convention** — services agree not to touch each other's tables but the database does not enforce this. Ownership is a social contract, not a technical constraint. Violations are silent and hard to detect. Rejected for the same reasons as above.

**Separate Postgres instance per service** — strongest isolation, closest to production microservices practice. Adds four Postgres containers to Docker Compose, increases memory and startup time, and requires four separate connection strings with no corresponding educational benefit at our scale. The single-instance, separate-schema approach provides equivalent logical isolation for our purposes.

## References
[1] Richardson, C., *Database per Service*, microservices.io. https://microservices.io/patterns/data/database-per-service.html
- Newman, S., *Building Microservices*, 2nd ed., 2021 — Chapter on data decomposition
- Gilbert & Lynch, *Brewer's Conjecture and the Feasibility of Consistent, Available, Partition-Tolerant Web Services*, 2002
