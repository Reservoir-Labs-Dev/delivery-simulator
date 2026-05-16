using System.Text;
using System.Text.Json;
using DeliveryService.Consumer;
using DeliveryService.Data;
using DeliveryService.Domain;
using DeliveryService.Handlers;
using DeliveryService.Simulation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace DeliveryService.Tests;

/// <summary>
/// End-to-end integration test: publishes <c>order.ready</c> to the real broker,
/// runs a real DeliveryConsumer in-process, asserts <c>delivery.completed</c> is
/// emitted back to <c>orders.exchange</c>. Skipped when RabbitMQ is unreachable.
/// </summary>
public class DeliveryConsumerIntegrationTests
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
        PublisherClientName = "delivery-tests-publisher",
    };

    private static readonly DeliveryConsumerOptions ConsumerOpts = new()
    {
        QueueName = $"delivery.queue.test-{Guid.NewGuid():N}",
        DeadLetterQueue = $"delivery.dlq.test-{Guid.NewGuid():N}",
        ConsumesRoutingKey = RoutingKeys.OrderReady,
        PrefetchCount = 10,
    };

    [SkippableFact(Timeout = 30_000)]
    public async Task End_to_end_order_ready_produces_delivery_completed()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Broker), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        using var verifier = RabbitMqVerifierQueue.Open(Broker, RoutingKeys.DeliveryCompleted, "delivery-tests-verifier");
        using var host = BuildHost();
        await host.StartAsync();

        await WaitForConsumerStartAsync(TimeSpan.FromSeconds(5));

        var orderId = Guid.NewGuid();
        var inboundEventId = Guid.NewGuid();

        try
        {
            PublishOrderReady(orderId, inboundEventId);

            var (props, body) = verifier.WaitForOne(TimeSpan.FromSeconds(10));

            props.Type.Should().Be(RoutingKeys.DeliveryCompleted);
            props.ContentType.Should().Be("application/json");
            props.DeliveryMode.Should().Be(2);

            var completed = JsonSerializer.Deserialize<DeliveryCompletedEvent>(
                Encoding.UTF8.GetString(body),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            completed.Should().NotBeNull();
            completed!.EventType.Should().Be(RoutingKeys.DeliveryCompleted);
            completed.OrderId.Should().Be(orderId);
            completed.AttemptNumber.Should().Be(1);
            completed.DeliveryId.Should().NotBeNullOrWhiteSpace();
            completed.EventId.Should().NotBe(Guid.Empty);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            var stored = await db.Deliveries.SingleAsync(d => d.OrderId == orderId);
            stored.Status.Should().Be(DeliveryStatus.Completed);
            stored.CompletedAt.Should().NotBeNull();
        }
        finally
        {
            CleanupQueues();
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));

                services.AddDbContext<DeliveryDbContext>(opt =>
                    opt.UseInMemoryDatabase($"delivery-{Guid.NewGuid()}")
                       .ConfigureWarnings(w => w.Ignore(
                           Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

                services.Configure<RabbitMqOptions>(opt =>
                {
                    opt.HostName = Broker.HostName;
                    opt.Port = Broker.Port;
                    opt.UserName = Broker.UserName;
                    opt.Password = Broker.Password;
                    opt.VirtualHost = Broker.VirtualHost;
                    opt.Exchange = Broker.Exchange;
                    opt.DeadLetterExchange = Broker.DeadLetterExchange;
                    opt.PublisherClientName = "delivery-tests-host";
                });

                services.Configure<DeliveryConsumerOptions>(opt =>
                {
                    opt.QueueName = ConsumerOpts.QueueName;
                    opt.DeadLetterQueue = ConsumerOpts.DeadLetterQueue;
                    opt.ConsumesRoutingKey = ConsumerOpts.ConsumesRoutingKey;
                    opt.PrefetchCount = ConsumerOpts.PrefetchCount;
                });

                services.Configure<DeliverySimulatorOptions>(opt =>
                {
                    opt.MinDelayMs = 0;
                    opt.MaxDelayMs = 5;
                    opt.SuccessProbability = 1.0;
                });

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IDeliverySimulator, DeliverySimulator>();
                services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
                services.AddScoped<OrderReadyHandler>();
                services.AddHostedService<DeliveryConsumer>();
            })
            .Build();
    }

    private static void PublishOrderReady(Guid orderId, Guid eventId)
    {
        var factory = new ConnectionFactory
        {
            HostName = Broker.HostName,
            Port = Broker.Port,
            UserName = Broker.UserName,
            Password = Broker.Password,
            VirtualHost = Broker.VirtualHost,
            ClientProvidedName = "delivery-tests-ready-producer",
        };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();
        channel.ExchangeDeclare(Broker.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();

        var now = DateTimeOffset.UtcNow;
        var payload = new OrderReadyEvent(
            EventId: eventId,
            EventType: RoutingKeys.OrderReady,
            OccurredAt: now,
            OrderId: orderId,
            PreparedAt: now,
            PrepDurationMs: 350,
            Items: Array.Empty<ReadyItem>());

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var props = channel.CreateBasicProperties();
        props.ContentType = "application/json";
        props.DeliveryMode = 2;
        props.MessageId = eventId.ToString();
        props.Type = RoutingKeys.OrderReady;

        channel.BasicPublish(Broker.Exchange, RoutingKeys.OrderReady, mandatory: false, basicProperties: props, body: body);
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
                    ClientProvidedName = "delivery-tests-startup-probe",
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
            $"DeliveryConsumer did not declare its queue '{ConsumerOpts.QueueName}' within {timeout}.");
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
                ClientProvidedName = "delivery-tests-cleanup",
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();
            channel.QueueDelete(ConsumerOpts.QueueName, ifUnused: false, ifEmpty: false);
            channel.QueueDelete(ConsumerOpts.DeadLetterQueue, ifUnused: false, ifEmpty: false);
        }
        catch
        {
            /* best-effort cleanup */
        }
    }
}
