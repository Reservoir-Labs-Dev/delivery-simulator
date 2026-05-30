using System.Collections.Concurrent;
using System.Net.Http.Json;
using DashboardApi.Consumer;
using DashboardApi.Handlers;
using DashboardApi.Hubs;
using DeliveryService.Consumer;
using DeliveryService.Data;
using DeliveryService.Handlers;
using DeliveryService.Simulation;
using FluentAssertions;
using KitchenService.Consumer;
using KitchenService.Data;
using KitchenService.Handlers;
using KitchenService.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderService.Api;
using OrderService.Data;
using OrderService.Handlers;
using PaymentService.Consumer;
using PaymentService.Data;
using PaymentService.Handlers;
using PaymentService.Simulation;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace E2E.Tests;

/// <summary>
/// DOG-34 + DOG-36 end-to-end happy-path smoke test.
///
/// Spins up all four pipeline services + dashboard-api in a single
/// in-process <see cref="WebApplication"/> on a random Kestrel port. Connects
/// a real SignalR client to the dashboard's /hubs/orders. POSTs one order to
/// the OrderService endpoint. Asserts four OrderStatusChanged broadcasts
/// arrive in the correct order with the correct payloads.
///
/// This verifies the design from ADR-007: pipeline services publish their
/// usual domain events, dashboard-api translates each one into an
/// OrderStatusChanged notification, every state transition reaches the hub.
///
/// Skipped when RabbitMQ isn't reachable. Each run uses Guid-suffixed queue
/// names so concurrent runs don't interfere; queues are deleted on teardown.
/// </summary>
public sealed class HappyPathSmokeTests : IAsyncLifetime
{
    private static readonly RabbitMqOptions Broker = new()
    {
        HostName = "localhost",
        Port = 5672,
        UserName = "reservoir",
        Password = "reservoir",
        VirtualHost = "/",
        Exchange = "orders.exchange",
        DeadLetterExchange = "orders.dlx",
        PublisherClientName = "e2e-tests",
    };

    // Unique queue names per run so concurrent test runs don't fight.
    private readonly string _suffix = $"e2e-{Guid.NewGuid():N}";
    private readonly PaymentConsumerOptions _payment;
    private readonly KitchenConsumerOptions _kitchen;
    private readonly DeliveryConsumerOptions _delivery;
    private readonly DashboardConsumerOptions _dashboard;

    private WebApplication? _app;
    private HubConnection? _hub;

    public HappyPathSmokeTests()
    {
        _payment = new PaymentConsumerOptions
        {
            QueueName = $"payment.queue.{_suffix}",
            DeadLetterQueue = $"payment.dlq.{_suffix}",
            ConsumesRoutingKey = RoutingKeys.OrderCreated,
            PrefetchCount = 10,
        };
        _kitchen = new KitchenConsumerOptions
        {
            QueueName = $"kitchen.queue.{_suffix}",
            DeadLetterQueue = $"kitchen.dlq.{_suffix}",
            ConsumesRoutingKey = RoutingKeys.PaymentSucceeded,
            PrefetchCount = 10,
        };
        _delivery = new DeliveryConsumerOptions
        {
            QueueName = $"delivery.queue.{_suffix}",
            DeadLetterQueue = $"delivery.dlq.{_suffix}",
            ConsumesRoutingKey = RoutingKeys.OrderReady,
            PrefetchCount = 10,
        };
        _dashboard = new DashboardConsumerOptions
        {
            QueueName = $"dashboard.queue.{_suffix}",
            PrefetchCount = 50,
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
        CleanupQueues();
    }

    [SkippableFact(Timeout = 30_000)]
    public async Task POST_orders_produces_4_status_broadcasts_in_correct_order()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Broker), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        _app = BuildPipelineApp();
        await _app.StartAsync();

        var baseUrl = ResolveBaseUrl(_app);

        await WaitForAllConsumerQueuesAsync(TimeSpan.FromSeconds(10));

        var received = new BlockingCollection<OrderStatusChangedNotification>();
        _hub = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}{OrdersHub.Path}")
            .Build();
        _hub.On<OrderStatusChangedNotification>(OrdersHub.ClientMethod, received.Add);
        await _hub.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var request = new CreateOrderRequest(
            CustomerId: "cust-e2e",
            Currency: "USD",
            Items: new[]
            {
                new CreateOrderItemRequest("item-burger", "Cheeseburger", 2, 850),
                new CreateOrderItemRequest("item-fries",  "Fries",        1, 300),
            });

        var response = await http.PostAsJsonAsync("/orders", request);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
        created.Should().NotBeNull();
        var orderId = created!.OrderId;

        var collected = new List<OrderStatusChangedNotification>();
        for (var i = 0; i < 4; i++)
        {
            if (!received.TryTake(out var item, TimeSpan.FromSeconds(10)))
                throw new TimeoutException(
                    $"Only received {collected.Count}/4 OrderStatusChanged broadcasts. " +
                    $"Got: [{string.Join(", ", collected.Select(c => c.Status))}]");
            collected.Add(item);
        }

        collected.Should().AllSatisfy(n => n.OrderId.Should().Be(orderId));

        var statusesInOrder = collected.Select(n => n.Status).ToList();
        statusesInOrder.Should().Equal(new[]
        {
            OrderStatus.Created,
            OrderStatus.PaymentSucceeded,
            OrderStatus.OrderReady,
            OrderStatus.Delivered,
        });

        var sourceServicesInOrder = collected.Select(n => n.SourceService).ToList();
        sourceServicesInOrder.Should().Equal(new[]
        {
            "OrderService",
            "PaymentService",
            "KitchenService",
            "DeliveryService",
        });
    }

    private WebApplication BuildPipelineApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "E2EPipeline" });
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var services = builder.Services;

        services.AddDbContext<OrdersDbContext>(opt => opt
            .UseInMemoryDatabase($"orders-{_suffix}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<PaymentsDbContext>(opt => opt
            .UseInMemoryDatabase($"payments-{_suffix}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<KitchenDbContext>(opt => opt
            .UseInMemoryDatabase($"kitchen-{_suffix}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<DeliveryDbContext>(opt => opt
            .UseInMemoryDatabase($"delivery-{_suffix}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        services.Configure<RabbitMqOptions>(opt =>
        {
            opt.HostName = Broker.HostName;
            opt.Port = Broker.Port;
            opt.UserName = Broker.UserName;
            opt.Password = Broker.Password;
            opt.VirtualHost = Broker.VirtualHost;
            opt.Exchange = Broker.Exchange;
            opt.DeadLetterExchange = Broker.DeadLetterExchange;
            opt.PublisherClientName = "e2e-tests-publisher";
        });
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<PaymentConsumerOptions>(o => Copy(o, _payment));
        services.Configure<KitchenConsumerOptions>(o => Copy(o, _kitchen));
        services.Configure<DeliveryConsumerOptions>(o => Copy(o, _delivery));
        services.Configure<DashboardConsumerOptions>(o => Copy(o, _dashboard));

        services.Configure<PaymentSimulatorOptions>(o => { o.MinDelayMs = 0; o.MaxDelayMs = 5; o.SuccessProbability = 1.0; });
        services.Configure<KitchenSimulatorOptions>(o => { o.MinPrepMs = 0; o.MaxPrepMs = 5; });
        services.Configure<DeliverySimulatorOptions>(o => { o.MinDelayMs = 0; o.MaxDelayMs = 5; o.SuccessProbability = 1.0; });
        services.AddSingleton<IPaymentSimulator, PaymentSimulator>();
        services.AddSingleton<IKitchenSimulator, KitchenSimulator>();
        services.AddSingleton<IDeliverySimulator, DeliverySimulator>();

        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<PaymentService.Handlers.OrderCreatedHandler>();
        services.AddScoped<PaymentSucceededHandler>();
        services.AddScoped<OrderReadyHandler>();
        services.AddSingleton<StatusTranslator>();
        services.AddSingleton<IOrderStatusBroadcaster, SignalROrderStatusBroadcaster>();

        services.AddHostedService<PaymentConsumer>();
        services.AddHostedService<KitchenConsumer>();
        services.AddHostedService<DeliveryConsumer>();
        services.AddHostedService<StatusEventsConsumer>();

        services.AddSignalR();

        var app = builder.Build();

        app.MapHub<OrdersHub>(OrdersHub.Path);

        app.MapPost("/orders", async (CreateOrderRequest req, CreateOrderHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(req, ct);
            return Results.Created($"/orders/{result.OrderId}", result);
        });

        return app;
    }

    private static string ResolveBaseUrl(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("No server addresses feature on Kestrel host.");
        var url = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel host did not bind any address.");
        return url.TrimEnd('/');
    }

    private async Task WaitForAllConsumerQueuesAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var queues = new[] { _payment.QueueName, _kitchen.QueueName, _delivery.QueueName, _dashboard.QueueName };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var conn = TestConnectionFactory("e2e-tests-startup-probe").CreateConnection();
                using var channel = conn.CreateModel();
                foreach (var q in queues) channel.QueueDeclarePassive(q);
                return;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
        throw new TimeoutException($"Consumers did not declare all queues within {timeout}. Queues: {string.Join(", ", queues)}");
    }

    private void CleanupQueues()
    {
        try
        {
            using var conn = TestConnectionFactory("e2e-tests-cleanup").CreateConnection();
            using var channel = conn.CreateModel();
            foreach (var q in new[]
            {
                _payment.QueueName, _payment.DeadLetterQueue,
                _kitchen.QueueName, _kitchen.DeadLetterQueue,
                _delivery.QueueName, _delivery.DeadLetterQueue,
                _dashboard.QueueName,
            })
            {
                try { channel.QueueDelete(q, ifUnused: false, ifEmpty: false); } catch { /* best-effort */ }
            }
        }
        catch
        {
            /* broker may already be down on teardown */
        }
    }

    private static ConnectionFactory TestConnectionFactory(string clientName) => new()
    {
        HostName = Broker.HostName,
        Port = Broker.Port,
        UserName = Broker.UserName,
        Password = Broker.Password,
        VirtualHost = Broker.VirtualHost,
        ClientProvidedName = clientName,
    };

    private static void Copy(PaymentConsumerOptions to, PaymentConsumerOptions from)
    {
        to.QueueName = from.QueueName;
        to.DeadLetterQueue = from.DeadLetterQueue;
        to.ConsumesRoutingKey = from.ConsumesRoutingKey;
        to.PrefetchCount = from.PrefetchCount;
    }

    private static void Copy(KitchenConsumerOptions to, KitchenConsumerOptions from)
    {
        to.QueueName = from.QueueName;
        to.DeadLetterQueue = from.DeadLetterQueue;
        to.ConsumesRoutingKey = from.ConsumesRoutingKey;
        to.PrefetchCount = from.PrefetchCount;
    }

    private static void Copy(DeliveryConsumerOptions to, DeliveryConsumerOptions from)
    {
        to.QueueName = from.QueueName;
        to.DeadLetterQueue = from.DeadLetterQueue;
        to.ConsumesRoutingKey = from.ConsumesRoutingKey;
        to.PrefetchCount = from.PrefetchCount;
    }

    private static void Copy(DashboardConsumerOptions to, DashboardConsumerOptions from)
    {
        to.QueueName = from.QueueName;
        to.PrefetchCount = from.PrefetchCount;
        to.SubscribesTo = from.SubscribesTo;
    }
}
