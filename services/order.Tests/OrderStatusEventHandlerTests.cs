using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Data;
using OrderService.Data.Models;
using OrderService.Handlers;
using OrderService.Tests.TestSupport;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Serialization;
using Reservoir.TestSupport;

namespace OrderService.Tests;

public class OrderStatusEventHandlerTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

    private OrderStatusEventHandler NewHandler(out OrdersDbContext db)
    {
        db = InMemoryDb.Create();
        return new OrderStatusEventHandler(db, _clock, NullLogger<OrderStatusEventHandler>.Instance);
    }

    private static Order SeedOrder(OrdersDbContext db, Guid orderId, string status = OrderStatus.Created)
    {
        var order = new Order
        {
            Id = orderId,
            CustomerId = "cust-0042",
            Status = status,
            TotalAmountCents = 2000,
            Currency = "USD",
            CreatedAt = new DateTimeOffset(2026, 5, 16, 11, 59, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 16, 11, 59, 0, TimeSpan.Zero),
        };
        db.Orders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static byte[] Serialize<T>(T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, EventJsonOptions.Web);

    [Fact]
    public async Task PaymentSucceeded_advances_status_and_records_event_id()
    {
        var handler = NewHandler(out var db);
        var orderId = Guid.NewGuid();
        SeedOrder(db, orderId);

        var evt = new PaymentSucceededEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: _clock.GetUtcNow(),
            OrderId: orderId,
            PaymentId: "pay-1",
            AmountChargedCents: 2000,
            Currency: "USD",
            AttemptNumber: 1);

        var result = await handler.HandleAsync(RoutingKeys.PaymentSucceeded, Serialize(evt), CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.PreviousStatus.Should().Be(OrderStatus.Created);
        result.NewStatus.Should().Be(OrderStatus.PaymentSucceeded);

        db.Orders.Single().Status.Should().Be(OrderStatus.PaymentSucceeded);
        db.Orders.Single().UpdatedAt.Should().Be(_clock.GetUtcNow());

        var processed = db.ProcessedEventIds.Single();
        processed.EventId.Should().Be(evt.EventId);
        processed.ProcessedAt.Should().Be(_clock.GetUtcNow());
    }

    [Theory]
    [InlineData(RoutingKeys.PaymentFailed, OrderStatus.PaymentFailed)]
    [InlineData(RoutingKeys.OrderReady, OrderStatus.OrderReady)]
    [InlineData(RoutingKeys.DeliveryCompleted, OrderStatus.Delivered)]
    [InlineData(RoutingKeys.DeliveryFailed, OrderStatus.DeliveryFailed)]
    public async Task Each_routing_key_maps_to_the_correct_status(string routingKey, string expectedStatus)
    {
        var handler = NewHandler(out var db);
        var orderId = Guid.NewGuid();
        SeedOrder(db, orderId);
        var now = _clock.GetUtcNow();

        byte[] body = routingKey switch
        {
            RoutingKeys.PaymentFailed     => Serialize(new PaymentFailedEvent(Guid.NewGuid(), routingKey, now, orderId, "PAYMENT_DECLINED", 1, false)),
            RoutingKeys.OrderReady        => Serialize(new OrderReadyEvent(Guid.NewGuid(), routingKey, now, orderId, now, 250, Array.Empty<ReadyItem>())),
            RoutingKeys.DeliveryCompleted => Serialize(new DeliveryCompletedEvent(Guid.NewGuid(), routingKey, now, orderId, "del-1", now, 1)),
            RoutingKeys.DeliveryFailed    => Serialize(new DeliveryFailedEvent(Guid.NewGuid(), routingKey, now, orderId, "DRIVER_UNAVAILABLE", 3, true)),
            _ => throw new InvalidOperationException("unreachable"),
        };

        var result = await handler.HandleAsync(routingKey, body, CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.NewStatus.Should().Be(expectedStatus);
        db.Orders.Single().Status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Skips_duplicate_event_and_does_not_update_status_again()
    {
        var handler = NewHandler(out var db);
        var orderId = Guid.NewGuid();
        SeedOrder(db, orderId);
        var eventId = Guid.NewGuid();
        var evt = new PaymentSucceededEvent(
            EventId: eventId,
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: _clock.GetUtcNow(),
            OrderId: orderId,
            PaymentId: "pay-1",
            AmountChargedCents: 2000,
            Currency: "USD",
            AttemptNumber: 1);

        await handler.HandleAsync(RoutingKeys.PaymentSucceeded, Serialize(evt), CancellationToken.None);

        // A "later" event tries to come in on the *same* eventId (a redelivery).
        // The order is now PAYMENT_SUCCEEDED. Even though the payload would tell
        // us to set it to PAYMENT_SUCCEEDED again, the handler should short-circuit
        // before touching the row.
        var spoofedReplay = evt with { OccurredAt = _clock.GetUtcNow().AddMinutes(5) };
        var second = await handler.HandleAsync(RoutingKeys.PaymentSucceeded, Serialize(spoofedReplay), CancellationToken.None);

        second.Skipped.Should().BeTrue();
        db.Orders.Single().Status.Should().Be(OrderStatus.PaymentSucceeded);
        db.ProcessedEventIds.Count().Should().Be(1);
    }

    [Fact]
    public async Task Unknown_order_records_event_id_but_does_not_throw()
    {
        var handler = NewHandler(out var db);
        // No SeedOrder — the orders table is empty.
        var evt = new PaymentSucceededEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: _clock.GetUtcNow(),
            OrderId: Guid.NewGuid(),
            PaymentId: "pay-1",
            AmountChargedCents: 2000,
            Currency: "USD",
            AttemptNumber: 1);

        var result = await handler.HandleAsync(RoutingKeys.PaymentSucceeded, Serialize(evt), CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.PreviousStatus.Should().BeNull();
        result.NewStatus.Should().BeNull();
        db.Orders.Should().BeEmpty();
        db.ProcessedEventIds.Single().EventId.Should().Be(evt.EventId);
    }

    [Fact]
    public async Task Empty_event_id_throws()
    {
        var handler = NewHandler(out var db);
        var orderId = Guid.NewGuid();
        SeedOrder(db, orderId);

        var evt = new PaymentSucceededEvent(
            EventId: Guid.Empty,
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: _clock.GetUtcNow(),
            OrderId: orderId,
            PaymentId: "pay-1",
            AmountChargedCents: 2000,
            Currency: "USD",
            AttemptNumber: 1);

        var act = async () => await handler.HandleAsync(
            RoutingKeys.PaymentSucceeded, Serialize(evt), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
