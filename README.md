# Reservoir Labs — Fault-Tolerant Event-Driven Order Processing System

KIU bachelor thesis project. A web-based simulator demonstrating fault-tolerance patterns in an event-driven distributed system, using a food order pipeline as the domain.

## Architecture

Four ASP.NET Core (.NET 8) microservices communicating exclusively via RabbitMQ:

- **Order Service** — accepts orders, publishes `order.created`
- **Payment Service** — consumes `order.created`, publishes `payment.succeeded` / `payment.failed`
- **Kitchen Service** — consumes `payment.succeeded`, publishes `order.ready`
- **Delivery Service** — consumes `order.ready`, publishes `delivery.completed` / `delivery.failed`

Each service has its own PostgreSQL schema. A React/Blazor dashboard streams live order state via SignalR. A chaos engine injects 5 failure scenarios at runtime to test reliability patterns: dead-letter queues, exponential-backoff retry, and idempotent consumers.

## Repository layout

/services/order Order Service (ASP.NET Core)
/services/payment Payment Service
/services/kitchen Kitchen Service
/services/delivery Delivery Service
/dashboard Real-time web frontend (React or Blazor)
/docker Docker Compose + per-service Dockerfiles
/docs/adr Architecture Decision Records (ADR-001..)
/docs/arch Architecture reference (ARCH-001..)
/docs/admin KIU paperwork, supervisor correspondence
/docs/research Literature notes and summaries
/docs/experiments Experiment plans and raw results
/docs/thesis Thesis chapters, bibliography, figures

## Running locally

(To be filled in once Docker Compose is set up — DOG task.)

```bash
docker compose up -d
```

## Project management

- **Linear:** issue tracker, milestones M0–M6 — https://linear.app
- **Slack:** team coordination
- **GitHub:** monorepo, PR reviews

## Team

Two students, KIU CS program. Defense July 2026.

## License

Academic project. Not licensed for redistribution.
