using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.TestSupport;

namespace OrderService.Tests;

/// <summary>
/// Integration test: drives the real <see cref="RabbitMqEventPublisher"/> against
/// a local broker. Skipped automatically when the broker is unreachable.
/// </summary>
public class RabbitMqEventPublisherIntegrationTests
{
    private static readonly RabbitMqOptions Options = new()
    {
        HostName = "localhost",
        Port = 5672,
        UserName = "reservoir",
        Password = "reservoir",
        VirtualHost = "/",
        Exchange = "orders.exchange",
        DeadLetterExchange = "orders.dlx",
        PublisherClientName = "order-service-tests",
    };

    [SkippableFact]
    public void Publish_routes_order_created_to_orders_exchange_and_consumer_receives_payload()
    {
        Skip.IfNot(BrokerProbe.IsAvailable(Options), "RabbitMQ not reachable at localhost:5672 (run `docker compose up -d`).");

        using var verifier = RabbitMqVerifierQueue.Open(Options, RoutingKeys.OrderCreated, clientName: "order-tests-verifier");

        using var sut = new RabbitMqEventPublisher(
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<RabbitMqEventPublisher>.Instance);

        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        var payload = new OrderCreatedEvent(
            EventId: eventId,
            EventType: RoutingKeys.OrderCreated,
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

        sut.Publish(RoutingKeys.OrderCreated, payload, eventId, occurredAt);

        var (props, body) = verifier.WaitForOne(TimeSpan.FromSeconds(5));

        props.MessageId.Should().Be(eventId.ToString());
        props.ContentType.Should().Be("application/json");
        props.DeliveryMode.Should().Be(2);
        props.Type.Should().Be(RoutingKeys.OrderCreated);

        var json = Encoding.UTF8.GetString(body);
        var parsed = JsonSerializer.Deserialize<OrderCreatedEvent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        parsed.Should().NotBeNull();
        parsed!.EventId.Should().Be(eventId);
        parsed.EventType.Should().Be(RoutingKeys.OrderCreated);
        parsed.OrderId.Should().Be(orderId);
        parsed.CustomerId.Should().Be("cust-integration");
        parsed.TotalAmountCents.Should().Be(2000);
        parsed.Items.Should().HaveCount(2);
    }
}
