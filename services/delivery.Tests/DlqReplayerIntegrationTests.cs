using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace DeliveryService.Tests;

/// <summary>
/// Integration test for the DOG-134 recovery mechanism. Seeds a dead-letter
/// queue directly, runs <see cref="DlqReplayer.Drain"/>, and asserts the message
/// is republished to <c>orders.exchange</c>/<c>order.ready</c> with its
/// idempotency key intact and a fresh retry budget, and that the DLQ is emptied.
/// Skipped when RabbitMQ is unreachable.
/// </summary>
public class DlqReplayerIntegrationTests
{
    private static readonly RabbitMqOptions Broker = new()
    {
        HostName = "localhost",
        Port = 5672,
        UserName = "reservoir",
        Password = "reservoir",
        VirtualHost = "/",
        Exchange = "orders.exchange",
        PublisherClientName = "dlq-replay-tests-publisher",
    };

    private static readonly string DlqName = $"delivery.dlq.replaytest-{Guid.NewGuid():N}";

    [SkippableFact]
    public void Drain_replays_dlq_message_to_target_with_key_preserved_and_retry_reset()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Broker), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        using var verifier = RabbitMqVerifierQueue.Open(Broker, RoutingKeys.OrderReady, "dlq-replay-tests-verifier");

        var messageId = Guid.NewGuid().ToString();
        var body = Encoding.UTF8.GetBytes("""{"orderId":"replay-me"}""");

        var factory = new ConnectionFactory
        {
            HostName = Broker.HostName,
            Port = Broker.Port,
            UserName = Broker.UserName,
            Password = Broker.Password,
            VirtualHost = Broker.VirtualHost,
            ClientProvidedName = "dlq-replay-tests-seed",
        };

        try
        {
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            // Seed the DLQ with a "dead" message that already exhausted its retries.
            channel.QueueDeclare(DlqName, durable: true, exclusive: false, autoDelete: false);
            var seedProps = channel.CreateBasicProperties();
            seedProps.ContentType = "application/json";
            seedProps.DeliveryMode = 2;
            seedProps.MessageId = messageId;
            seedProps.Type = RoutingKeys.OrderReady;
            seedProps.Headers = new Dictionary<string, object> { [ConsumerRetry.RetryCountHeader] = 3 };
            channel.BasicPublish(exchange: string.Empty, routingKey: DlqName, basicProperties: seedProps, body: body);

            // Act: replay.
            var replayed = DlqReplayer.Drain(
                channel, DlqName, Broker.Exchange, RoutingKeys.OrderReady, NullLogger.Instance);

            replayed.Should().Be(1);

            // The replayed message lands on the main exchange under order.ready.
            var (props, replayedBody) = verifier.WaitForOne(TimeSpan.FromSeconds(10));
            Encoding.UTF8.GetString(replayedBody).Should().Be(Encoding.UTF8.GetString(body));
            props.MessageId.Should().Be(messageId, "the idempotency key must survive replay");
            props.DeliveryMode.Should().Be(2);
            ConsumerRetry.ReadRetryCount(props).Should().Be(0, "replay grants a fresh retry budget");

            // The DLQ is now empty.
            channel.BasicGet(DlqName, autoAck: true).Should().BeNull();
        }
        finally
        {
            try
            {
                using var conn = factory.CreateConnection();
                using var channel = conn.CreateModel();
                channel.QueueDelete(DlqName, ifUnused: false, ifEmpty: false);
            }
            catch { /* best-effort cleanup */ }
        }
    }
}
