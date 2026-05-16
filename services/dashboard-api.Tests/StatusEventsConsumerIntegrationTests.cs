using System.Text.Json;
using DashboardApi.Consumer;
using DashboardApi.Handlers;
using DashboardApi.Hubs;
using DashboardApi.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace DashboardApi.Tests;

/// <summary>
/// End-to-end integration test: publishes one event of each routing key
/// the dashboard cares about and asserts the broadcaster receives the
/// translated <see cref="OrderStatusChangedNotification"/> for each.
/// Skipped automatically when RabbitMQ isn't reachable.
/// </summary>
public class StatusEventsConsumerIntegrationTests
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
        PublisherClientName = "dashboard-tests-publisher",
    };

    private static readonly DashboardConsumerOptions ConsumerOpts = new()
    {
        QueueName = $"dashboard.queue.test-{Guid.NewGuid():N}",
        PrefetchCount = 50,
    };

    [SkippableFact(Timeout = 30_000)]
    public async Task End_to_end_published_events_become_signalr_broadcasts()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Broker), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        var fakeBroadcaster = new FakeOrderStatusBroadcaster();
        using var host = BuildHost(fakeBroadcaster);
        await host.StartAsync();

        await WaitForConsumerStartAsync(TimeSpan.FromSeconds(5));

        var orderId = Guid.NewGuid();

        try
        {
            PublishOrderCreated(orderId);
            PublishPaymentSucceeded(orderId);
            PublishOrderReady(orderId);
            PublishDeliveryCompleted(orderId);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (fakeBroadcaster.Broadcasts.Count < 4 && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            fakeBroadcaster.Broadcasts.Should().HaveCount(4);

            var byStatus = fakeBroadcaster.Broadcasts.ToDictionary(b => b.Status);
            byStatus.Should().ContainKeys(
                OrderStatus.Created,
                OrderStatus.PaymentSucceeded,
                OrderStatus.OrderReady,
                OrderStatus.Delivered);

            byStatus[OrderStatus.Created].OrderId.Should().Be(orderId);
            byStatus[OrderStatus.Created].SourceService.Should().Be("OrderService");
            byStatus[OrderStatus.PaymentSucceeded].SourceService.Should().Be("PaymentService");
            byStatus[OrderStatus.OrderReady].SourceService.Should().Be("KitchenService");
            byStatus[OrderStatus.Delivered].SourceService.Should().Be("DeliveryService");
        }
        finally
        {
            CleanupQueues();
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static IHost BuildHost(IOrderStatusBroadcaster broadcaster)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));

                services.Configure<RabbitMqOptions>(opt =>
                {
                    opt.HostName = Broker.HostName;
                    opt.Port = Broker.Port;
                    opt.UserName = Broker.UserName;
                    opt.Password = Broker.Password;
                    opt.VirtualHost = Broker.VirtualHost;
                    opt.Exchange = Broker.Exchange;
                    opt.DeadLetterExchange = Broker.DeadLetterExchange;
                    opt.PublisherClientName = "dashboard-tests-host";
                });

                services.Configure<DashboardConsumerOptions>(opt =>
                {
                    opt.QueueName = ConsumerOpts.QueueName;
                    opt.PrefetchCount = ConsumerOpts.PrefetchCount;
                });

                services.AddSingleton<StatusTranslator>();
                services.AddSingleton(broadcaster);
                services.AddHostedService<StatusEventsConsumer>();
            })
            .Build();
    }

    private static void PublishOrderCreated(Guid orderId)
    {
        var payload = new OrderCreatedEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.OrderCreated,
            OccurredAt: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: "cust-int",
            Items: new[] { new OrderEventItem("item-x", "X", 1, 100) },
            TotalAmountCents: 100,
            Currency: "USD");
        Publish(RoutingKeys.OrderCreated, payload, payload.EventId);
    }

    private static void PublishPaymentSucceeded(Guid orderId)
    {
        var payload = new PaymentSucceededEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: DateTimeOffset.UtcNow,
            OrderId: orderId,
            PaymentId: Guid.NewGuid().ToString(),
            AmountChargedCents: 100,
            Currency: "USD",
            AttemptNumber: 1);
        Publish(RoutingKeys.PaymentSucceeded, payload, payload.EventId);
    }

    private static void PublishOrderReady(Guid orderId)
    {
        var payload = new OrderReadyEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.OrderReady,
            OccurredAt: DateTimeOffset.UtcNow,
            OrderId: orderId,
            PreparedAt: DateTimeOffset.UtcNow,
            PrepDurationMs: 350,
            Items: Array.Empty<ReadyItem>());
        Publish(RoutingKeys.OrderReady, payload, payload.EventId);
    }

    private static void PublishDeliveryCompleted(Guid orderId)
    {
        var payload = new DeliveryCompletedEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.DeliveryCompleted,
            OccurredAt: DateTimeOffset.UtcNow,
            OrderId: orderId,
            DeliveryId: Guid.NewGuid().ToString(),
            DeliveredAt: DateTimeOffset.UtcNow,
            AttemptNumber: 1);
        Publish(RoutingKeys.DeliveryCompleted, payload, payload.EventId);
    }

    private static void Publish<T>(string routingKey, T payload, Guid eventId) where T : class
    {
        var factory = new ConnectionFactory
        {
            HostName = Broker.HostName,
            Port = Broker.Port,
            UserName = Broker.UserName,
            Password = Broker.Password,
            VirtualHost = Broker.VirtualHost,
            ClientProvidedName = $"dashboard-tests-publisher-{routingKey}",
        };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();
        channel.ExchangeDeclare(Broker.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var props = channel.CreateBasicProperties();
        props.ContentType = "application/json";
        props.DeliveryMode = 2;
        props.MessageId = eventId.ToString();
        props.Type = routingKey;

        channel.BasicPublish(Broker.Exchange, routingKey, mandatory: false, basicProperties: props, body: body);
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForConsumerStartAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = Broker.HostName,
                    Port = Broker.Port,
                    UserName = Broker.UserName,
                    Password = Broker.Password,
                    VirtualHost = Broker.VirtualHost,
                    ClientProvidedName = "dashboard-tests-startup-probe",
                };
                using var conn = factory.CreateConnection();
                using var channel = conn.CreateModel();
                channel.QueueDeclarePassive(ConsumerOpts.QueueName);
                return;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
        throw new TimeoutException(
            $"StatusEventsConsumer did not declare its queue '{ConsumerOpts.QueueName}' within {timeout}.");
    }

    private static void CleanupQueues()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = Broker.HostName,
                Port = Broker.Port,
                UserName = Broker.UserName,
                Password = Broker.Password,
                VirtualHost = Broker.VirtualHost,
                ClientProvidedName = "dashboard-tests-cleanup",
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();
            channel.QueueDelete(ConsumerOpts.QueueName, ifUnused: false, ifEmpty: false);
        }
        catch
        {
            /* best-effort cleanup */
        }
    }
}
