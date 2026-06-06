using KitchenService.Chaos;
using KitchenService.Consumer;
using KitchenService.Data;
using KitchenService.Handlers;
using KitchenService.Simulation;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<KitchenDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("KitchenDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=kitchen";
    opt.UseNpgsql(conn);
});

builder.Services.AddRabbitMqPublisher(builder.Configuration);
builder.Services.PostConfigure<RabbitMqOptions>(opt => opt.PublisherClientName = "kitchen-service-publisher");

builder.Services.Configure<KitchenConsumerOptions>(
    builder.Configuration.GetSection(KitchenConsumerOptions.SectionName));
builder.Services.Configure<KitchenSimulatorOptions>(
    builder.Configuration.GetSection(KitchenSimulatorOptions.SectionName));

// Read-only view of chaos.chaos_config (owned by dashboard-api). DbContextFactory
// so the singleton DbChaosConfigReader rents a fresh context per message.
builder.Services.AddDbContextFactory<ChaosConfigDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("ChaosConfigDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=chaos";
    opt.UseNpgsql(conn);
});
builder.Services.AddSingleton<IChaosConfigReader, DbChaosConfigReader>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<KitchenSimulator>();
builder.Services.AddSingleton<IKitchenSimulator>(sp => new ChaosAwareKitchenSimulator(
    sp.GetRequiredService<KitchenSimulator>(),
    sp.GetRequiredService<IChaosConfigReader>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ChaosAwareKitchenSimulator>>()));
builder.Services.AddScoped<PaymentSucceededHandler>();

builder.Services.AddHostedService<KitchenConsumer>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KitchenDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "kitchen" }));

app.Run();

public partial class Program { }
