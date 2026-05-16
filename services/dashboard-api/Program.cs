using DashboardApi.Consumer;
using DashboardApi.Handlers;
using DashboardApi.Hubs;
using Reservoir.BuildingBlocks.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.Configure<DashboardConsumerOptions>(
    builder.Configuration.GetSection(DashboardConsumerOptions.SectionName));

builder.Services.AddSingleton<StatusTranslator>();
builder.Services.AddSingleton<IOrderStatusBroadcaster, SignalROrderStatusBroadcaster>();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "dashboard-api" }));

app.MapHub<OrdersHub>(OrdersHub.Path);

app.Run();

public partial class Program { }
