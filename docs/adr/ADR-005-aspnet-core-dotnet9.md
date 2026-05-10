# ADR-005: ASP.NET Core (.NET 9) for All Microservices

## Status
Accepted

## Context
We need a framework and runtime for implementing four microservices. Each service has two responsibilities: exposing a small HTTP API (for health checks, chaos configuration, and in the case of OrderService, accepting orders) and running a long-lived background consumer that reads from a RabbitMQ queue. The choice affects development speed, library availability, deployment size, and how well it supports the patterns we need to implement (hosted consumers, EF Core, SignalR).

## Decision
All four microservices are implemented in C# on ASP.NET Core targeting .NET 9. Each service uses the minimal API model for HTTP endpoints and `IHostedService` for the RabbitMQ consumer worker. EF Core 9 is used for database access.

## Rationale

**IHostedService is a first-class primitive for background consumers.** A RabbitMQ consumer is a long-lived background process — it starts with the application, runs a message loop, and shuts down cleanly on application stop. ASP.NET Core's `IHostedService` (and the higher-level `BackgroundService` base class) is exactly this abstraction. The host manages lifetime, cancellation tokens, and graceful shutdown automatically. Implementing the same pattern in other frameworks requires either a third-party library or manual thread management.

**EF Core integrates naturally with the per-service schema decision (ADR-004).** EF Core supports schema-scoped `DbContext` configuration via `HasDefaultSchema()`. Migrations are per-`DbContext`, so each service has an independent migration history. `Database.MigrateAsync()` at startup applies pending migrations before the consumer starts processing. This is a well-documented, well-tooled pattern with no equivalent in lighter-weight stacks.

**SignalR is built into ASP.NET Core.** The dashboard requires real-time push of `OrderStatusChanged` events to browser clients (DOG-33). ASP.NET Core SignalR ships as a first-party package, is trivial to add to an existing minimal API host, and has a mature JavaScript client. Using a different backend runtime would require a separate SignalR-compatible server or a third-party WebSocket library.

**Minimal API reduces boilerplate without sacrificing structure.** ASP.NET Core minimal APIs (`app.MapPost(...)`, `app.MapGet(...)`) are sufficient for our small HTTP surface — OrderService needs `POST /orders` and `GET /orders/{id}`; all services need `GET /health` and `POST /chaos/set`. There is no need for full MVC controller scaffolding. Minimal API keeps each service's `Program.cs` concise and readable, which is useful for the thesis code walkthrough.

**Single language across the whole codebase.** Both team members work in C# across all services, the dashboard (if Blazor is chosen for DOG-21), and test code. There is no context-switching between languages or runtimes. Shared types (event payload records, common interfaces) can live in a shared project if needed without a serialisation boundary.

**.NET 9 is the current LTS-adjacent release.** .NET 9 is the latest release as of the project start date. All NuGet packages used (`RabbitMQ.Client`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.AspNetCore.SignalR`) have stable .NET 9 compatible versions. Using the current release avoids pinning to an older runtime and keeps tooling (SDK, CLI, Docker base images) consistent.

**Docker base images are small and well-maintained.** The official `mcr.microsoft.com/dotnet/aspnet:9.0` runtime image is suitable for production containers and is actively maintained by Microsoft. Multi-stage builds (`sdk` image to build, `aspnet` image to run) keep final image sizes reasonable. This integrates cleanly with our Docker Compose setup (DOG-16).

## Project structure per service

Each service follows the same layout:

```
services/<name>/
  Program.cs              -- host setup, DI registration, route mapping
  Consumer/
    <Name>Consumer.cs     -- BackgroundService: RabbitMQ message loop
  Handlers/
    <EventName>Handler.cs -- business logic per event type
  Data/
    <Name>DbContext.cs    -- EF Core DbContext, schema-scoped
    Migrations/           -- EF Core migration files
  Models/
    <DomainEntity>.cs     -- domain records
  Events/
    <EventName>.cs        -- event payload records (shared shape)
  <Name>Service.csproj
  appsettings.json
  appsettings.Development.json
```

The `Consumer` registers as a hosted service. The `Handler` is called by the consumer after the idempotency check passes. The `DbContext` is injected into handlers via `IServiceScopeFactory` (hosted services are singletons; DbContext is scoped — a factory scope is required).

## Trade-offs

**Heavier runtime than Go or Node for simple consumers.** A Go or Node.js RabbitMQ consumer would have a smaller binary and faster cold-start time. For our Docker Compose workload this is irrelevant — services are long-lived and cold-start time is not a metric we measure. The richness of the .NET ecosystem (EF Core, SignalR, hosted services) outweighs the runtime size difference at our scale.

**IServiceScopeFactory pattern adds a small amount of complexity.** Because `BackgroundService` is registered as a singleton but `DbContext` is scoped, handlers cannot have `DbContext` injected directly into the consumer constructor. A factory scope must be created per message. This is a well-known .NET pattern, but it adds a few lines of boilerplate to each consumer and is a potential source of confusion. It is documented in the implementation notes for DOG-29 through DOG-32.

**Blazor dashboard dependency on .NET if chosen.** If DOG-21 resolves to Blazor (rather than React), the dashboard is also an ASP.NET Core application. This keeps the stack uniform but means the dashboard cannot be deployed independently of a .NET runtime. If React is chosen, the dashboard is a separate Node-based build with no .NET dependency.

## Alternatives rejected

**Node.js / TypeScript** — good RabbitMQ client (`amqplib`), but no `IHostedService` equivalent. TypeScript ORM options (`Prisma`, `TypeORM`) are viable but less integrated with a migration-first workflow. Team is more productive in C#.

**Python / FastAPI** — lightweight and fast to prototype. Async RabbitMQ (`aio-pika`) is workable. SQLAlchemy is a capable ORM. However, Python's type system is weaker than C#'s, which increases the chance of runtime errors in event payload handling. Not a language either team member uses for systems work.

## References
- Microsoft, *ASP.NET Core documentation*, https://learn.microsoft.com/aspnet/core
- Microsoft, *Background tasks with hosted services*, https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
- Microsoft, *EF Core migrations*, https://learn.microsoft.com/ef/core/managing-schemas/migrations
