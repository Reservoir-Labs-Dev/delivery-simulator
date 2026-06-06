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
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "payment" }));

app.Run();

public partial class Program { }
