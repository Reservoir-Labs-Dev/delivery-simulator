using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentService.Consumer;
using PaymentService.Data;
using PaymentService.Domain;
using PaymentService.Handlers;
using PaymentService.Simulation;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace PaymentService.Tests;

/// <summary>
/// End-to-end integration test: publishes <c>order.created</c> to the real broker,
/// runs a real PaymentConsumer in-process, asserts <c>payment.succeeded</c> is
/// emitted back to <c>orders.exchange</c>. Skipped when RabbitMQ is unreachable.
/// </summary>
public class PaymentConsumerIntegrationTests
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
        PublisherClientName = "payment-tests-publisher",
    };

    private static readonly PaymentConsumerOptions ConsumerOpts = new()
    {
        QueueName = $"payment.queue.test-{Guid.NewGuid():N}",
        DeadLetterQueue = $"payment.dlq.test-{Guid.NewGuid():N}",
        ConsumesRoutingKey = RoutingKeys.OrderCreated,
        PrefetchCount = 10,
    };

    [SkippableFact(Timeout = 30_000)]
    public async Task End_to_end_order_created_produces_payment_succeeded()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Broker), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        using var paymentVerifier = RabbitMqVerifierQueue.Open(Broker, RoutingKeys.PaymentSucceeded, "payment-tests-verifier");
        using var host = BuildHost();
        await host.StartAsync();

        await WaitForConsumerStartAsync(TimeSpan.FromSeconds(5));

        var orderId = Guid.NewGuid();
        var inboundEventId = Guid.NewGuid();

        try
        {
            PublishOrderCreated(orderId, inboundEventId);

            var (props, body) = paymentVerifier.WaitForOne(TimeSpan.FromSeconds(10));

            props.Type.Should().Be(RoutingKeys.PaymentSucceeded);
            props.ContentType.Should().Be("application/json");
            props.DeliveryMode.Should().Be(2);

            var paymentEvent = JsonSerializer.Deserialize<PaymentSucceededEvent>(
                Encoding.UTF8.GetString(body),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            paymentEvent.Should().NotBeNull();
            paymentEvent!.EventType.Should().Be(RoutingKeys.PaymentSucceeded);
            paymentEvent.OrderId.Should().Be(orderId);
            paymentEvent.AmountChargedCents.Should().Be(2000);
            paymentEvent.Currency.Should().Be("USD");
            paymentEvent.AttemptNumber.Should().Be(1);
            paymentEvent.PaymentId.Should().NotBeNullOrWhiteSpace();
            paymentEvent.EventId.Should().NotBe(Guid.Empty);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var stored = await db.PaymentRecords.SingleAsync(p => p.OrderId == orderId);
            stored.Status.Should().Be(PaymentStatus.Succeeded);
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
                var dbName = $"payments-{Guid.NewGuid()}";
                services.AddDbContext<PaymentsDbContext>(opt =>
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
                    opt.PublisherClientName = "payment-tests-host";
                });

                services.Configure<PaymentConsumerOptions>(opt =>
                {
                    opt.QueueName = ConsumerOpts.QueueName;
                    opt.DeadLetterQueue = ConsumerOpts.DeadLetterQueue;
                    opt.ConsumesRoutingKey = ConsumerOpts.ConsumesRoutingKey;
                    opt.PrefetchCount = ConsumerOpts.PrefetchCount;
                });

                services.Configure<PaymentSimulatorOptions>(opt =>
                {
                    opt.MinDelayMs = 0;
                    opt.MaxDelayMs = 5;
                    opt.SuccessProbability = 1.0;
                });

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IPaymentSimulator, PaymentSimulator>();
                services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
                services.AddScoped<OrderCreatedHandler>();
                services.AddSingleton<Reservoir.BuildingBlocks.Persistence.IMetricsWriter, Reservoir.TestSupport.FakeMetricsWriter>();
                services.AddHostedService<PaymentConsumer>();
            })
            .Build();
    }

    private static void PublishOrderCreated(Guid orderId, Guid eventId)
    {
        var factory = new ConnectionFactory
        {
            HostName = Broker.HostName,
            Port = Broker.Port,
            UserName = Broker.UserName,
            Password = Broker.Password,
            VirtualHost = Broker.VirtualHost,
            ClientProvidedName = "payment-tests-order-producer",
        };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();
        channel.ExchangeDeclare(Broker.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();

        var payload = new OrderCreatedEvent(
            EventId: eventId,
            EventType: RoutingKeys.OrderCreated,
            OccurredAt: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: "cust-integration",
            Items: new[]
            {
                new OrderEventItem("item-burger", "Cheeseburger", 2, 850),
                new OrderEventItem("item-fries",  "Fries",        1, 300),
            },
            TotalAmountCents: 2000,
            Currency: "USD");

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var props = channel.CreateBasicProperties();
        props.ContentType = "application/json";
        props.DeliveryMode = 2;
        props.MessageId = eventId.ToString();
        props.Type = RoutingKeys.OrderCreated;

        channel.BasicPublish(Broker.Exchange, RoutingKeys.OrderCreated, mandatory: false, basicProperties: props, body: body);
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
                    ClientProvidedName = "payment-tests-startup-probe",
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
            $"PaymentConsumer did not declare its queue '{ConsumerOpts.QueueName}' within {timeout}.");
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
                ClientProvidedName = "payment-tests-cleanup",
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
