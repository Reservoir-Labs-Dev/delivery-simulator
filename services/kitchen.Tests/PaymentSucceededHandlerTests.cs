using FluentAssertions;
using KitchenService.Domain;
using KitchenService.Handlers;
using KitchenService.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.TestSupport;

namespace KitchenService.Tests;

public class PaymentSucceededHandlerTests
{
    private readonly FakeEventPublisher _publisher = new();
    private readonly FakeKitchenSimulator _simulator = new() { DurationMsToReport = 250 };
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMetricsWriter _metrics = new();

    private PaymentSucceededHandler NewHandler(out KitchenService.Data.KitchenDbContext db)
    {
        db = InMemoryDb.Create();
        return new PaymentSucceededHandler(db, _publisher, _simulator, _clock, _metrics, NullLogger<PaymentSucceededHandler>.Instance);
    }

    private static PaymentSucceededEvent NewPaymentEvent(Guid? eventId = null, Guid? orderId = null) =>
        new(
            EventId: eventId ?? Guid.NewGuid(),
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: new DateTimeOffset(2026, 5, 16, 11, 59, 0, TimeSpan.Zero),
            OrderId: orderId ?? Guid.NewGuid(),
            PaymentId: Guid.NewGuid().ToString(),
            AmountChargedCents: 2000,
            Currency: "USD",
            AttemptNumber: 1);

    [Fact]
    public async Task Happy_path_persists_ready_kitchen_order_and_publishes_order_ready()
    {
        var handler = NewHandler(out var db);
        var inbound = NewPaymentEvent();

        var result = await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.PrepDurationMs.Should().Be(250);

        var record = db.KitchenOrders.Single();
        record.Id.Should().Be(result.KitchenOrderId);
        record.OrderId.Should().Be(inbound.OrderId);
        record.Status.Should().Be(KitchenOrderStatus.Ready);
        record.PrepStartedAt.Should().Be(_clock.GetUtcNow());
        record.PrepReadyAt.Should().Be(_clock.GetUtcNow());
        record.PrepDurationMs.Should().Be(250);

        _publisher.Published.Should().HaveCount(1);
        var pub = _publisher.Published[0];
        pub.RoutingKey.Should().Be(RoutingKeys.OrderReady);
        pub.MessageId.Should().Be(result.OutboundEventId);
        var payload = pub.Payload.Should().BeOfType<OrderReadyEvent>().Subject;
        payload.EventType.Should().Be(RoutingKeys.OrderReady);
        payload.OrderId.Should().Be(inbound.OrderId);
        payload.PrepDurationMs.Should().Be(250);
        payload.PreparedAt.Should().Be(_clock.GetUtcNow());
        payload.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Inserts_processed_event_id_on_success()
    {
        var handler = NewHandler(out var db);
        var inbound = NewPaymentEvent();

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
        var inbound = NewPaymentEvent();

        await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);
        _publisher.Published.Clear();
        _simulator.CallCount = 0;

        var second = await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        second.Skipped.Should().BeTrue();
        _publisher.Published.Should().BeEmpty();
        _simulator.CallCount.Should().Be(0);
        db.KitchenOrders.Count().Should().Be(1);
        db.ProcessedEventIds.Count().Should().Be(1);
    }

    [Fact]
    public async Task Outbound_event_ids_are_unique_per_publish()
    {
        var handler = NewHandler(out _);

        var r1 = await handler.HandleAsync(NewPaymentEvent(), attemptNumber: 1, CancellationToken.None);
        var r2 = await handler.HandleAsync(NewPaymentEvent(), attemptNumber: 1, CancellationToken.None);

        r1.OutboundEventId.Should().NotBe(Guid.Empty);
        r2.OutboundEventId.Should().NotBe(Guid.Empty);
        r1.OutboundEventId.Should().NotBe(r2.OutboundEventId);
        r1.KitchenOrderId.Should().NotBe(r2.KitchenOrderId);
    }

    [Fact]
    public async Task Empty_event_id_is_rejected()
    {
        var handler = NewHandler(out _);
        var inbound = NewPaymentEvent(eventId: Guid.Empty);

        var act = () => handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*empty eventId*");
    }
}
