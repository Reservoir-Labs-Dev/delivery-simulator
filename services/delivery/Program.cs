using DeliveryService.Chaos;
using DeliveryService.Consumer;
using DeliveryService.Data;
using DeliveryService.Handlers;
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

builder.Services.AddHostedService<DeliveryConsumer>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "delivery" }));

app.Run();

public partial class Program { }
