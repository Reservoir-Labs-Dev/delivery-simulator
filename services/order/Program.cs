using Microsoft.EntityFrameworkCore;
using OrderService.Api;
using OrderService.Data;
using OrderService.Handlers;
using OrderService.Messaging;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<OrdersDbContext>(opt =>
{
    var conn = builder.Configuration.GetConnectionString("OrdersDb")
        ?? "Host=localhost;Port=5432;Database=reservoir;Username=reservoir;Password=reservoir;Search Path=orders";
    opt.UseNpgsql(conn);
});

builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CreateOrderHandler>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
