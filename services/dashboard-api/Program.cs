using DashboardApi.Api;
using DashboardApi.Consumer;
using DashboardApi.Handlers;
using DashboardApi.Hubs;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.AddDbContext<ChaosConfigDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("ChaosConfigDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=chaos";
    opt.UseNpgsql(conn);
});

// Read-only access to metrics.metrics (owned by the consumer services).
// dashboard-api only needs to stream rows out via /metrics/export.csv.
builder.Services.AddDbContextFactory<MetricsDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("MetricsDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=metrics";
    opt.UseNpgsql(conn);
});

builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.Configure<DashboardConsumerOptions>(
    builder.Configuration.GetSection(DashboardConsumerOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StatusTranslator>();
builder.Services.AddSingleton<IOrderStatusBroadcaster, SignalROrderStatusBroadcaster>();
builder.Services.AddScoped<ChaosConfigUpsertHandler>();

builder.Services.AddHostedService<StatusEventsConsumer>();

builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(b => b
        .AllowAnyHeader()
        .AllowAnyMethod()
        .SetIsOriginAllowed(_ => true)
        .AllowCredentials());
});

var app = builder.Build();

// Auto-create the chaos schema + chaos_config table on startup. We can't reuse
// EnsureCreatedAsync here: every service shares the same `reservoir` database,
// so by the time dashboard-api starts the DB already has tables (orders.*,
// payments.*, ...) and EnsureCreatedAsync short-circuits without creating
// ours. Idempotent DDL is the most honest dev-path fix; swap to migrations
// once the project adopts them.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChaosConfigDbContext>();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE SCHEMA IF NOT EXISTS chaos;
        CREATE TABLE IF NOT EXISTS chaos.chaos_config (
            name        varchar(128) PRIMARY KEY,
            enabled     boolean      NOT NULL,
            params      jsonb        NOT NULL DEFAULT '{{}}'::jsonb,
            updated_at  timestamp with time zone NOT NULL
        );
        """);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "dashboard-api" }));

app.MapPost("/chaos/set", async (
    ChaosConfigSetRequest request,
    ChaosConfigUpsertHandler handler,
    CancellationToken ct) =>
{
    try
    {
        var row = await handler.HandleAsync(request, ct);
        return Results.Ok(new
        {
            name = row.Name,
            enabled = row.Enabled,
            @params = row.Params,
            updatedAt = row.UpdatedAt
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetChaosConfig");

// Read side for the DOG-49 Chaos Control Panel. The dashboard polls this every
// few seconds to mirror DB state — covers the case where chaos was toggled in
// another browser tab or directly via psql.
app.MapGet("/chaos/list", async (
    ChaosConfigDbContext db,
    CancellationToken ct) =>
{
    var rows = await db.ChaosConfigs
        .AsNoTracking()
        .OrderBy(c => c.Name)
        .Select(c => new
        {
            name = c.Name,
            enabled = c.Enabled,
            @params = c.Params,
            updatedAt = c.UpdatedAt
        })
        .ToListAsync(ct);
    return Results.Ok(rows);
})
.WithName("ListChaosConfig");

// DOG-51 metrics CSV export. M4 experiments curl this to derive per-stage
// latency, retry distribution, and DLQ rates. Returned as text/csv with a
// stable header so spreadsheets and pandas read it without configuration.
// The query streams rows ordered by started_at ASC so an analyst can pipe
// the output into pandas without re-sorting.
app.MapGet("/metrics/export.csv", async (
    IDbContextFactory<MetricsDbContext> metricsFactory,
    CancellationToken ct) =>
{
    await using var db = await metricsFactory.CreateDbContextAsync(ct);
    var rows = await db.Metrics
        .AsNoTracking()
        .OrderBy(m => m.StartedAt)
        .Select(m => new
        {
            m.Id,
            m.OrderId,
            m.ServiceName,
            m.StartedAt,
            m.CompletedAt,
            m.RetryCount,
            m.Outcome
        })
        .ToListAsync(ct);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("id,order_id,service_name,started_at,completed_at,duration_ms,retry_count,outcome");
    foreach (var r in rows)
    {
        var durationMs = (long)(r.CompletedAt - r.StartedAt).TotalMilliseconds;
        sb.Append(r.Id).Append(',')
          .Append(r.OrderId).Append(',')
          .Append(r.ServiceName).Append(',')
          .Append(r.StartedAt.ToString("O")).Append(',')
          .Append(r.CompletedAt.ToString("O")).Append(',')
          .Append(durationMs).Append(',')
          .Append(r.RetryCount).Append(',')
          .Append(r.Outcome).Append('\n');
    }
    return Results.Text(sb.ToString(), "text/csv");
})
.WithName("ExportMetricsCsv");

app.MapHub<OrdersHub>(OrdersHub.Path);

app.Run();

public partial class Program { }
