using DeliveryService.Chaos;
using DeliveryService.Consumer;
using DeliveryService.Data;
using DeliveryService.Handlers;
using DeliveryService.Recovery;
using DeliveryService.Simulation;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<DeliveryDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("DeliveryDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=delivery";
    opt.UseNpgsql(conn);
});

// Read-only view of chaos.chaos_config (owned by dashboard-api). DbContextFactory
// so the singleton DbChaosConfigReader rents a fresh context per message.
builder.Services.AddDbContextFactory<ChaosConfigDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("ChaosConfigDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=chaos";
    opt.UseNpgsql(conn);
});
builder.Services.AddSingleton<IChaosConfigReader, DbChaosConfigReader>();

// Metrics writer (DOG-51). DbContextFactory + singleton writer so handler
// invocations get a fresh context per write without DI scope ceremony.
builder.Services.AddDbContextFactory<MetricsDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("MetricsDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=metrics";
    opt.UseNpgsql(conn);
});
builder.Services.AddSingleton<IMetricsWriter, DbMetricsWriter>();

builder.Services.AddRabbitMqPublisher(builder.Configuration);
builder.Services.PostConfigure<RabbitMqOptions>(opt => opt.PublisherClientName = "delivery-service-publisher");

builder.Services.Configure<DeliveryConsumerOptions>(
    builder.Configuration.GetSection(DeliveryConsumerOptions.SectionName));
builder.Services.Configure<DeliverySimulatorOptions>(
    builder.Configuration.GetSection(DeliverySimulatorOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeliverySimulator>();
builder.Services.AddSingleton<IDeliverySimulator>(sp => new ChaosAwareDeliverySimulator(
    sp.GetRequiredService<DeliverySimulator>(),
    sp.GetRequiredService<IChaosConfigReader>(),
    sp.GetRequiredService<ILogger<ChaosAwareDeliverySimulator>>()));
builder.Services.AddScoped<OrderReadyHandler>();

// DOG-134 recovery use case (drains delivery.dlq on demand).
builder.Services.AddSingleton<DlqReplayHandler>();

builder.Services.AddHostedService<DeliveryConsumer>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
    await db.Database.EnsureCreatedAsync();

    // Idempotent metrics schema bootstrap (DOG-51). Same rationale as the
    // chaos schema in dashboard-api: EnsureCreatedAsync short-circuits when
    // other tables already exist in the shared `reservoir` database.
    var metricsFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MetricsDbContext>>();
    await using var metricsDb = await metricsFactory.CreateDbContextAsync();
    await metricsDb.Database.ExecuteSqlRawAsync("""
        CREATE SCHEMA IF NOT EXISTS metrics;
        CREATE TABLE IF NOT EXISTS metrics.metrics (
            id            uuid        PRIMARY KEY,
            order_id      uuid        NOT NULL,
            service_name  varchar(32) NOT NULL,
            started_at    timestamp with time zone NOT NULL,
            completed_at  timestamp with time zone NOT NULL,
            retry_count   int         NOT NULL,
            outcome       varchar(32) NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_metrics_order_id     ON metrics.metrics (order_id);
        CREATE INDEX IF NOT EXISTS ix_metrics_service_name ON metrics.metrics (service_name);
        CREATE INDEX IF NOT EXISTS ix_metrics_started_at   ON metrics.metrics (started_at DESC);
        """);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "delivery" }));

// DOG-134 — recovery endpoint. Drains delivery.dlq and replays each message
// back onto orders.exchange/order.ready so a now-healed consumer reprocesses
// it. Idempotent (preserves the original MessageId), so safe to call repeatedly.
// Wiring lives in DlqReplayHandler; this is just the route.
app.MapPost("/admin/dlq/replay", (DlqReplayHandler handler) =>
    Results.Ok(new { service = "delivery", dlq = handler.DeadLetterQueue, replayed = handler.Replay() }));

app.Run();

public partial class Program { }
