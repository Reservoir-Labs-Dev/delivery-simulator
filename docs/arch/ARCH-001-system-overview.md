# ARCH-001: System Overview

**Project:** Fault-Tolerant Event-Driven Order Processing System
**Status:** Living document — update as decisions are finalized
**Last updated:** 2026-05-04

---

## 1. Service Inventory

Four ASP.NET Core (.NET 9) microservices. Services communicate exclusively via RabbitMQ — no direct HTTP calls between services.

| Service | Port | Publishes | Subscribes to | DB Schema |
|---|---|---|---|---|
| **OrderService** | 5294 | `order.created` | — | `orders` |
| **PaymentService** | 5203 | `payment.succeeded`, `payment.failed` | `order.created` | `payments` |
| **KitchenService** | 5233 | `order.ready` | `payment.succeeded` | `kitchen` |
| **DeliveryService** | 5097 | `delivery.completed`, `delivery.failed` | `order.ready` | `delivery` |

### Service responsibilities

**OrderService**
Entry point for the pipeline. Exposes `POST /orders` to clients. Persists the new order to its own schema and publishes `order.created` to the RabbitMQ topic exchange.

**PaymentService**
Consumes `order.created`. Simulates payment processing — succeeds or fails based on configured probability or chaos settings. Publishes the result event. Payment failure halts the pipeline for that order.

**KitchenService**
Consumes `payment.succeeded`. Simulates food preparation time. Publishes `order.ready` when preparation completes. Ignored if payment failed.

**DeliveryService**
Consumes `order.ready`. Simulates dispatch. Can fail under chaos scenario 2 (delivery failure loop). Terminal stage — publishes `delivery.completed` or `delivery.failed`.

---

## 2. End-to-End Flow

```
Client
  │
  │  POST /orders (HTTP)
  ▼
┌─────────────┐
│ OrderService │──── order.created ────►┌──────────────────┐
└─────────────┘                         │  PaymentService   │
                                        └──────────────────┘
                                               │
                               ┌───────────────┴───────────────┐
                               │                               │
                        payment.succeeded              payment.failed
                               │                               │
                               ▼                          (pipeline halts)
                        ┌──────────────┐
                        │ KitchenService│──── order.ready ────►┌──────────────────┐
                        └──────────────┘                       │  DeliveryService  │
                                                               └──────────────────┘
                                                                       │
                                                       ┌───────────────┴───────────────┐
                                                       │                               │
                                               delivery.completed              delivery.failed
                                                       │                               │
                                               (order fulfilled)              (terminal failure)
```

### Failure handling at every stage

Each consumer implements three reliability patterns before an event reaches the DLQ:

1. **Idempotency check** — consumer queries `processed_event_ids`. If the event ID already exists, the message is acknowledged and skipped without reprocessing.
2. **Exponential backoff retry** — on failure, the consumer retries up to 3 times with delays of 1 s, 2 s, 4 s.
3. **Dead Letter Queue** — after 3 failed attempts, RabbitMQ routes the message via the Dead Letter Exchange (DLX) to the service's DLQ for inspection and manual replay.

---

## 3. Tech Stack

| Layer | Technology | Notes |
|---|---|---|
| Runtime | .NET 9 / ASP.NET Core | All four microservices. Minimal API + `IHostedService` consumer workers. |
| Message broker | RabbitMQ 3.x | Topic exchange (`orders`). DLX per service queue for failed messages. |
| Database | PostgreSQL 16 | One logical schema per service. EF Core for ORM. |
| Dashboard | React or Blazor (TBD — DOG-21) | Real-time order state view. |
| Real-time push | SignalR | `OrderStatusChanged` events broadcast to dashboard clients on every state transition. |
| Containerisation | Docker + Docker Compose | All services, RabbitMQ, and Postgres run as containers locally. |
| API docs | OpenAPI (built-in) | Auto-generated via `Microsoft.AspNetCore.OpenApi`. Available at `/openapi` in Development. |
| Reliability | DLX + retry + idempotency | Dead-letter exchange, 3× exponential backoff (1 s / 2 s / 4 s), `processed_event_ids` deduplication table per service. |

---

## 4. Deployment Topology (Docker Compose)

All containers run on a shared Docker bridge network (`reservoir-net`). Services reference each other and infrastructure by container name — no hardcoded IPs.

```
┌─────────────────────────────────────────────────── docker-compose.yml ───┐
│  reservoir-net (bridge)                                                   │
│                                                                           │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐        │
│  │  order-service   │  │ payment-service  │  │ kitchen-service  │        │
│  │  :5294           │  │  :5203           │  │  :5233           │        │
│  │  ASP.NET Core    │  │  ASP.NET Core    │  │  ASP.NET Core    │        │
│  └──────────────────┘  └──────────────────┘  └──────────────────┘        │
│                                                                           │
│  ┌──────────────────┐  ┌──────────────────────────────────────┐          │
│  │ delivery-service │  │  dashboard                           │          │
│  │  :5097           │  │  :3000 (web)  :5000 (SignalR hub)    │          │
│  │  ASP.NET Core    │  │  React / Blazor (TBD)                │          │
│  └──────────────────┘  └──────────────────────────────────────┘          │
│                                                                           │
│  ┌──────────────────────────┐  ┌──────────────────────────────┐          │
│  │  rabbitmq                │  │  postgres                    │          │
│  │  :5672  (AMQP)           │  │  :5432                       │          │
│  │  :15672 (management UI)  │  │  schemas: orders, payments,  │          │
│  │  topic exchange: orders  │  │           kitchen, delivery  │          │
│  └──────────────────────────┘  └──────────────────────────────┘          │
└───────────────────────────────────────────────────────────────────────────┘
```

### Container dependencies (startup order)

```
postgres ──► all four services
rabbitmq ──► all four services
all four services ──► dashboard (SignalR hub must be reachable)
```

Docker Compose `depends_on` with `condition: service_healthy` should be used for Postgres and RabbitMQ to avoid race conditions on startup.

---

## 5. RabbitMQ Topology

**Exchange:** `orders` (topic, durable)

| Routing key | Consumer queue | DLQ |
|---|---|---|
| `order.created` | `payment.order.created` | `payment.order.created.dlq` |
| `payment.succeeded` | `kitchen.payment.succeeded` | `kitchen.payment.succeeded.dlq` |
| `order.ready` | `delivery.order.ready` | `delivery.order.ready.dlq` |

Each queue is bound to the `orders` exchange with its routing key. Each queue has a DLX argument pointing to a `orders.dlx` dead-letter exchange, which routes to the corresponding DLQ.

See ARCH-003 for full RabbitMQ topology detail.

---

## Related documents

- ADR-001 — Why event-driven over REST
- ARCH-002 — Service contracts (events & payloads)
- ARCH-003 — RabbitMQ topology
- ARCH-004 — Data model
- ARCH-005 — Chaos scenarios reference
