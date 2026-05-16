using DeliveryService.Domain;
using DeliveryService.Handlers;
using DeliveryService.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.TestSupport;

namespace DeliveryService.Tests;

public class OrderReadyHandlerTests
{
    private readonly FakeEventPublisher _publisher = new();
    private readonly FakeDeliverySimulator _simulator = new() { ShouldSucceed = true };
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

    private OrderReadyHandler NewHandler(out DeliveryService.Data.DeliveryDbContext db)
    {
        db = InMemoryDb.Create();
        return new OrderReadyHandler(db, _publisher, _simulator, _clock, NullLogger<OrderReadyHandler>.Instance);
    }

    private static OrderReadyEvent NewOrderReadyEvent(Guid? eventId = null, Guid? orderId = null) =>
        new(
            EventId: eventId ?? Guid.NewGuid(),
            EventType: RoutingKeys.OrderReady,
            OccurredAt: new DateTimeOffset(2026, 5, 16, 11, 59, 0, TimeSpan.Zero),
            OrderId: orderId ?? Guid.NewGuid(),
            PreparedAt: new DateTimeOffset(2026, 5, 16, 11, 59, 30, TimeSpan.Zero),
            PrepDurationMs: 350,
            Items: Array.Empty<ReadyItem>());

    [Fact]
    public async Task Happy_path_persists_completed_delivery_and_publishes_delivery_completed()
    {
        var handler = NewHandler(out var db);
        var inbound = NewOrderReadyEvent();

        var result = await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.Status.Should().Be(DeliveryStatus.Completed);

        var record = db.Deliveries.Single();
        record.Id.Should().Be(result.DeliveryId);
        record.OrderId.Should().Be(inbound.OrderId);
        record.Status.Should().Be(DeliveryStatus.Completed);
        record.AttemptNumber.Should().Be(1);
        record.FailureReason.Should().BeNull();
        record.StartedAt.Should().Be(_clock.GetUtcNow());
        record.CompletedAt.Should().Be(_clock.GetUtcNow());

        _publisher.Published.Should().HaveCount(1);
        var pub = _publisher.Published[0];
        pub.RoutingKey.Should().Be(RoutingKeys.DeliveryCompleted);
        pub.MessageId.Should().Be(result.OutboundEventId);
        var payload = pub.Payload.Should().BeOfType<DeliveryCompletedEvent>().Subject;
        payload.EventType.Should().Be(RoutingKeys.DeliveryCompleted);
        payload.OrderId.Should().Be(inbound.OrderId);
        payload.DeliveryId.Should().Be(record.Id.ToString());
        payload.DeliveredAt.Should().Be(_clock.GetUtcNow());
        payload.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public async Task Inserts_processed_event_id_on_success()
    {
        var handler = NewHandler(out var db);
        var inbound = NewOrderReadyEvent();

        await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        var processed = db.ProcessedEventIds.SingleOrDefault();
        processed.Should().NotBeNull();
        processed!.EventId.Should().Be(inbound.EventId);
        processed.ProcessedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Skips_duplicate_event_and_does_not_publish_or_persist_again()
    {
        var handler = NewHandler(out var db);
        var inbound = NewOrderReadyEvent();

        await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);
        _publisher.Published.Clear();
        _simulator.CallCount = 0;

        var second = await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        second.Skipped.Should().BeTrue();
        second.Status.Should().Be("SKIPPED");
        _publisher.Published.Should().BeEmpty();
        _simulator.CallCount.Should().Be(0);
        db.Deliveries.Count().Should().Be(1);
        db.ProcessedEventIds.Count().Should().Be(1);
    }

    [Fact]
    public async Task Failed_path_persists_failed_delivery_and_publishes_delivery_failed()
    {
        _simulator.ShouldSucceed = false;
        _simulator.FailureReason = DeliveryFailureReason.DriverUnavailable;
        var handler = NewHandler(out var db);
        var inbound = NewOrderReadyEvent();

        var result = await handler.HandleAsync(inbound, attemptNumber: 2, CancellationToken.None);

        result.Status.Should().Be(DeliveryStatus.Failed);

        var record = db.Deliveries.Single();
        record.Status.Should().Be(DeliveryStatus.Failed);
        record.FailureReason.Should().Be(DeliveryFailureReason.DriverUnavailable);
        record.AttemptNumber.Should().Be(2);

        _publisher.Published.Should().HaveCount(1);
        var pub = _publisher.Published[0];
        pub.RoutingKey.Should().Be(RoutingKeys.DeliveryFailed);
        var payload = pub.Payload.Should().BeOfType<DeliveryFailedEvent>().Subject;
        payload.OrderId.Should().Be(inbound.OrderId);
        payload.Reason.Should().Be(DeliveryFailureReason.DriverUnavailable);
        payload.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task Outbound_event_ids_are_unique_per_publish()
    {
        var handler = NewHandler(out _);

        var r1 = await handler.HandleAsync(NewOrderReadyEvent(), 1, CancellationToken.None);
        var r2 = await handler.HandleAsync(NewOrderReadyEvent(), 1, CancellationToken.None);

        r1.OutboundEventId.Should().NotBe(Guid.Empty);
        r2.OutboundEventId.Should().NotBe(Guid.Empty);
        r1.OutboundEventId.Should().NotBe(r2.OutboundEventId);
        r1.DeliveryId.Should().NotBe(r2.DeliveryId);
    }

    [Fact]
    public async Task Empty_event_id_is_rejected()
    {
        var handler = NewHandler(out _);
        var inbound = NewOrderReadyEvent(eventId: Guid.Empty);

        var act = () => handler.HandleAsync(inbound, 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*empty eventId*");
    }

    [Fact]
    public void ReadAttemptNumber_returns_1_when_no_header()
    {
        OrderReadyHandler.ReadAttemptNumber(null).Should().Be(1);
    }
}
