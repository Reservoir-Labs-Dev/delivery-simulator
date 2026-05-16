using Microsoft.EntityFrameworkCore;
using PaymentService.Consumer;
using PaymentService.Data;
using PaymentService.Handlers;
using PaymentService.Simulation;
using Reservoir.BuildingBlocks.Messaging;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<PaymentsDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("PaymentsDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=payments";
    opt.UseNpgsql(conn);
});

builder.Services.AddRabbitMqPublisher(builder.Configuration);
builder.Services.PostConfigure<RabbitMqOptions>(opt => opt.PublisherClientName = "payment-service-publisher");

builder.Services.Configure<PaymentConsumerOptions>(
    builder.Configuration.GetSection(PaymentConsumerOptions.SectionName));
builder.Services.Configure<PaymentSimulatorOptions>(
    builder.Configuration.GetSection(PaymentSimulatorOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPaymentSimulator, PaymentSimulator>();
builder.Services.AddScoped<OrderCreatedHandler>();

builder.Services.AddHostedService<PaymentConsumer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "payment" }));

app.Run();

public partial class Program { }
