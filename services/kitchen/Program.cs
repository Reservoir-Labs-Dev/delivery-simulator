using KitchenService.Consumer;
using KitchenService.Data;
using KitchenService.Handlers;
using KitchenService.Simulation;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Messaging;

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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IKitchenSimulator, KitchenSimulator>();
builder.Services.AddScoped<PaymentSucceededHandler>();

builder.Services.AddHostedService<KitchenConsumer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "kitchen" }));

app.Run();

public partial class Program { }
