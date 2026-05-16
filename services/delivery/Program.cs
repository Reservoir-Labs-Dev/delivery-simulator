using DeliveryService.Consumer;
using DeliveryService.Data;
using DeliveryService.Handlers;
using DeliveryService.Simulation;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Messaging;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<DeliveryDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("DeliveryDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=delivery";
    opt.UseNpgsql(conn);
});

builder.Services.AddRabbitMqPublisher(builder.Configuration);
builder.Services.PostConfigure<RabbitMqOptions>(opt => opt.PublisherClientName = "delivery-service-publisher");

builder.Services.Configure<DeliveryConsumerOptions>(
    builder.Configuration.GetSection(DeliveryConsumerOptions.SectionName));
builder.Services.Configure<DeliverySimulatorOptions>(
    builder.Configuration.GetSection(DeliverySimulatorOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeliverySimulator, DeliverySimulator>();
builder.Services.AddScoped<OrderReadyHandler>();

builder.Services.AddHostedService<DeliveryConsumer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "delivery" }));

app.Run();

public partial class Program { }
