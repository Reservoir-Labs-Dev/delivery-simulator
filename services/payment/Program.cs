using Microsoft.EntityFrameworkCore;
using PaymentService.Chaos;
using PaymentService.Consumer;
using PaymentService.Data;
using PaymentService.Handlers;
using PaymentService.Simulation;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<PaymentsDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("PaymentsDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=payments";
    opt.UseNpgsql(conn);
});

// Read-only view of chaos.chaos_config (owned by dashboard-api). Registered as
// a DbContextFactory so the singleton DbChaosConfigReader can rent a fresh
// context per message.
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
builder.Services.PostConfigure<RabbitMqOptions>(opt => opt.PublisherClientName = "payment-service-publisher");

builder.Services.Configure<PaymentConsumerOptions>(
    builder.Configuration.GetSection(PaymentConsumerOptions.SectionName));
builder.Services.Configure<PaymentSimulatorOptions>(
    builder.Configuration.GetSection(PaymentSimulatorOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PaymentSimulator>();
builder.Services.AddSingleton<IPaymentSimulator>(sp => new ChaosAwarePaymentSimulator(
    sp.GetRequiredService<PaymentSimulator>(),
    sp.GetRequiredService<IChaosConfigReader>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ChaosAwarePaymentSimulator>>()));
builder.Services.AddScoped<OrderCreatedHandler>();

builder.Services.AddHostedService<PaymentConsumer>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
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

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "payment" }));

app.Run();

public partial class Program { }
