using System.Text;
using System.Text.Json;
using FluentAssertions;
using KitchenService.Consumer;
using KitchenService.Data;
using KitchenService.Domain;
using KitchenService.Handlers;
using KitchenService.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace KitchenService.Tests;

/// <summary>
/// End-to-end integration test: publishes <c>payment.succeeded</c> to the real broker,
/// runs a real KitchenConsumer in-process, asserts <c>order.ready</c> is emitted back
/// to <c>orders.exchange</c>. Skipped when RabbitMQ is unreachable.
/// </summary>
public class KitchenConsumerIntegrationTests
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
        PublisherClientName = "kitchen-tests-publisher",
    };

    private static readonly KitchenConsumerOptions ConsumerOpts = new()
    {
        QueueName = $"kitchen.queue.test-{Guid.NewGuid():N}",
        DeadLetterQueue = $"kitchen.dlq.test-{Guid.NewGuid():N}",
        ConsumesRoutingKey = RoutingKeys.PaymentSucceeded,
        PrefetchCount = 10,
    };

    [SkippableFact(Timeout = 30_000)]
    public async Task End_to_end_payment_succeeded_produces_order_ready()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Broker), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        using var verifier = RabbitMqVerifierQueue.Open(Broker, RoutingKeys.OrderReady, "kitchen-tests-verifier");
        using var host = BuildHost();
        await host.StartAsync();

        await WaitForConsumerStartAsync(TimeSpan.FromSeconds(5));

        var orderId = Guid.NewGuid();
        var inboundEventId = Guid.NewGuid();

        try
        {
            PublishPaymentSucceeded(orderId, inboundEventId);

            var (props, body) = verifier.WaitForOne(TimeSpan.FromSeconds(10));

            props.Type.Should().Be(RoutingKeys.OrderReady);
            props.ContentType.Should().Be("application/json");
            props.DeliveryMode.Should().Be(2);

            var readyEvent = JsonSerializer.Deserialize<OrderReadyEvent>(
                Encoding.UTF8.GetString(body),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            readyEvent.Should().NotBeNull();
            readyEvent!.EventType.Should().Be(RoutingKeys.OrderReady);
            readyEvent.OrderId.Should().Be(orderId);
            readyEvent.PrepDurationMs.Should().BeGreaterOrEqualTo(0);
            readyEvent.EventId.Should().NotBe(Guid.Empty);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KitchenDbContext>();
            var stored = await db.KitchenOrders.SingleAsync(p => p.OrderId == orderId);
            stored.Status.Should().Be(KitchenOrderStatus.Ready);
            stored.PrepReadyAt.Should().NotBeNull();
            stored.PrepDurationMs.Should().NotBeNull();
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

                // Name fixed once per host: AddDbContext options are scoped, so a
                // Guid.NewGuid() *inside* the lambda would mint a different in-memory
                // database per DI scope — the consumer's writes would then be invisible
                // to the test's query scope.
                var dbName = $"kitchen-{Guid.NewGuid()}";
                services.AddDbContext<KitchenDbContext>(opt =>
                    opt.UseInMemoryDatabase(dbName)
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
                    opt.PublisherClientName = "kitchen-tests-host";
                });

                services.Configure<KitchenConsumerOptions>(opt =>
                {
                    opt.QueueName = ConsumerOpts.QueueName;
                    opt.DeadLetterQueue = ConsumerOpts.DeadLetterQueue;
                    opt.ConsumesRoutingKey = ConsumerOpts.ConsumesRoutingKey;
                    opt.PrefetchCount = ConsumerOpts.PrefetchCount;
                });

                services.Configure<KitchenSimulatorOptions>(opt =>
                {
                    opt.MinPrepMs = 0;
                    opt.MaxPrepMs = 5;
                });

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IKitchenSimulator, KitchenSimulator>();
                services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
                services.AddScoped<PaymentSucceededHandler>();
                services.AddSingleton<Reservoir.BuildingBlocks.Persistence.IMetricsWriter, Reservoir.TestSupport.FakeMetricsWriter>();
                services.AddHostedService<KitchenConsumer>();
            })
            .Build();
    }

    private static void PublishPaymentSucceeded(Guid orderId, Guid eventId)
    {
        var factory = new ConnectionFactory
        {
            HostName = Broker.HostName,
            Port = Broker.Port,
            UserName = Broker.UserName,
            Password = Broker.Password,
            VirtualHost = Broker.VirtualHost,
            ClientProvidedName = "kitchen-tests-payment-producer",
        };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();
        channel.ExchangeDeclare(Broker.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();

        var payload = new PaymentSucceededEvent(
            EventId: eventId,
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: DateTimeOffset.UtcNow,
            OrderId: orderId,
            PaymentId: Guid.NewGuid().ToString(),
            AmountChargedCents: 2000,
            Currency: "USD",
            AttemptNumber: 1);

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var props = channel.CreateBasicProperties();
        props.ContentType = "application/json";
        props.DeliveryMode = 2;
        props.MessageId = eventId.ToString();
        props.Type = RoutingKeys.PaymentSucceeded;

        channel.BasicPublish(Broker.Exchange, RoutingKeys.PaymentSucceeded, mandatory: false, basicProperties: props, body: body);
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
                    ClientProvidedName = "kitchen-tests-startup-probe",
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
            $"KitchenConsumer did not declare its queue '{ConsumerOpts.QueueName}' within {timeout}.");
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
                ClientProvidedName = "kitchen-tests-cleanup",
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
