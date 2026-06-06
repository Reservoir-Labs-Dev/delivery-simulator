using Microsoft.EntityFrameworkCore;
using OrderService.Api;
using OrderService.Chaos;
using OrderService.Consumer;
using OrderService.Data;
using OrderService.Handlers;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<OrdersDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("OrdersDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=orders";
    opt.UseNpgsql(conn);
});

// Read-only view of chaos.chaos_config (owned by dashboard-api). DbContextFactory
// so the singleton DbChaosConfigReader rents a fresh context per publish.
builder.Services.AddDbContextFactory<ChaosConfigDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("ChaosConfigDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=chaos";
    opt.UseNpgsql(conn);
});
builder.Services.AddSingleton<IChaosConfigReader, DbChaosConfigReader>();

// Register RabbitMqEventPublisher as the keyed inner publisher and expose
// ChaosAwareEventPublisher as IEventPublisher so the DOG-46 duplicate_events
// chaos scenario can republish order.created without touching the handler.
builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddSingleton<IEventPublisher>(sp => new ChaosAwareEventPublisher(
    sp.GetRequiredService<RabbitMqEventPublisher>(),
    sp.GetRequiredService<IChaosConfigReader>(),
    sp.GetRequiredService<ILogger<ChaosAwareEventPublisher>>()));
builder.Services.PostConfigure<RabbitMqOptions>(opt => opt.PublisherClientName = "order-service-publisher");

builder.Services.Configure<OrderStatusConsumerOptions>(
    builder.Configuration.GetSection(OrderStatusConsumerOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<OrderStatusEventHandler>();

builder.Services.AddHostedService<OrderStatusConsumer>();

var app = builder.Build();

// Auto-create the orders schema + tables on startup. EnsureCreatedAsync is the
// pragmatic dev path while there are no EF migrations on disk; swap to
// db.Database.MigrateAsync() once Initial migrations are generated per service.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "order" }));

app.MapPost("/orders", async (CreateOrderRequest request, CreateOrderHandler handler, CancellationToken ct) =>
{
    try
    {
        var result = await handler.HandleAsync(request, ct);
        return Results.Created($"/orders/{result.OrderId}", result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("CreateOrder");

app.Run();

public partial class Program { }
