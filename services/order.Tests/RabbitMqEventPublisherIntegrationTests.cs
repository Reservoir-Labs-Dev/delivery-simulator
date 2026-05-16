using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderService.Events;
using OrderService.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderService.Tests;

/// <summary>
/// Integration test: publishes a real <c>order.created</c> message through
/// RabbitMqEventPublisher to the local broker (docker compose up).
/// Skipped automatically when no broker is reachable on localhost:5672 so the
/// suite still passes on a fresh checkout.
/// </summary>
public class RabbitMqEventPublisherIntegrationTests
{
    private const string ExchangeName = "orders.exchange";
    private const string RoutingKey = "order.created";

    private static readonly RabbitMqOptions Options = new()
    {
        HostName = "localhost",
        Port = 5672,
        UserName = "reservoir",
        Password = "reservoir",
        VirtualHost = "/",
        Exchange = ExchangeName,
        DeadLetterExchange = "orders.dlx"
    };

    [SkippableFact]
    public void Publish_routes_order_created_to_orders_exchange_and_consumer_receives_payload()
    {
        Skip.IfNot(BrokerAvailable(), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        var testQueue = $"order-service-tests-{Guid.NewGuid():N}";
        using var verifier = OpenVerifierQueue(testQueue);

        using var sut = new RabbitMqEventPublisher(
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<RabbitMqEventPublisher>.Instance);

        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        var payload = new OrderCreatedEvent(
            EventId: eventId,
            EventType: RoutingKey,
            OccurredAt: occurredAt,
            OrderId: orderId,
            CustomerId: "cust-integration",
            Items: new[]
            {
                new OrderEventItem("item-burger", "Cheeseburger", 2, 850),
                new OrderEventItem("item-fries",  "Fries",        1, 300),
            },
            TotalAmountCents: 2000,
            Currency: "USD");

        sut.Publish(RoutingKey, payload, eventId, occurredAt);

        var (props, body) = verifier.WaitForOne(TimeSpan.FromSeconds(5));

        props.MessageId.Should().Be(eventId.ToString());
        props.ContentType.Should().Be("application/json");
        props.DeliveryMode.Should().Be(2);
        props.Type.Should().Be(RoutingKey);

        var json = Encoding.UTF8.GetString(body);
        var parsed = JsonSerializer.Deserialize<OrderCreatedEvent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        parsed.Should().NotBeNull();
        parsed!.EventId.Should().Be(eventId);
        parsed.EventType.Should().Be(RoutingKey);
        parsed.OrderId.Should().Be(orderId);
        parsed.CustomerId.Should().Be("cust-integration");
        parsed.TotalAmountCents.Should().Be(2000);
        parsed.Items.Should().HaveCount(2);
    }

    private static bool BrokerAvailable()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = Options.HostName,
                Port = Options.Port,
                UserName = Options.UserName,
                Password = Options.Password,
                VirtualHost = Options.VirtualHost,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
                SocketReadTimeout = TimeSpan.FromSeconds(2),
                SocketWriteTimeout = TimeSpan.FromSeconds(2)
            };
            using var conn = factory.CreateConnection("order-service-tests-probe");
            return conn.IsOpen;
        }
        catch
        {
            return false;
        }
    }

    private static VerifierQueue OpenVerifierQueue(string queueName)
    {
        var factory = new ConnectionFactory
        {
            HostName = Options.HostName,
            Port = Options.Port,
            UserName = Options.UserName,
            Password = Options.Password,
            VirtualHost = Options.VirtualHost,
        };

        var connection = factory.CreateConnection("order-service-tests-verifier");
        var channel = connection.CreateModel();

        channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.QueueDeclare(queueName, durable: false, exclusive: true, autoDelete: true);
        channel.QueueBind(queueName, ExchangeName, RoutingKey);

        return new VerifierQueue(connection, channel, queueName);
    }

    private sealed class VerifierQueue : IDisposable
    {
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly string _queueName;
        private readonly EventingBasicConsumer _consumer;
        private readonly System.Collections.Concurrent.BlockingCollection<(IBasicProperties Props, byte[] Body)> _received
            = new();

        public VerifierQueue(IConnection connection, IModel channel, string queueName)
        {
            _connection = connection;
            _channel = channel;
            _queueName = queueName;
            _consumer = new EventingBasicConsumer(_channel);
            _consumer.Received += (_, ea) =>
            {
                _received.Add((ea.BasicProperties, ea.Body.ToArray()));
                _channel.BasicAck(ea.DeliveryTag, multiple: false);
            };
            _channel.BasicConsume(_queueName, autoAck: false, _consumer);
        }

        public (IBasicProperties Props, byte[] Body) WaitForOne(TimeSpan timeout)
        {
            if (!_received.TryTake(out var item, timeout))
                throw new TimeoutException($"No message received on {_queueName} within {timeout}.");
            return item;
        }

        public void Dispose()
        {
            try { _channel?.Close(); } catch { }
            try { _connection?.Close(); } catch { }
            _channel?.Dispose();
            _connection?.Dispose();
            _received.Dispose();
        }
    }
}
