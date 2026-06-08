using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Domain;
using PaymentService.Handlers;
using PaymentService.Tests.TestSupport;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;
using Reservoir.TestSupport;

namespace PaymentService.Tests;

public class OrderCreatedHandlerTests
{
    private readonly FakeEventPublisher _publisher = new();
    private readonly FakePaymentSimulator _simulator = new() { ShouldSucceed = true };
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMetricsWriter _metrics = new();

    private OrderCreatedHandler NewHandler(out PaymentService.Data.PaymentsDbContext db)
    {
        db = InMemoryDb.Create();
        return new OrderCreatedHandler(db, _publisher, _simulator, _clock, _metrics, NullLogger<OrderCreatedHandler>.Instance);
    }

    private static OrderCreatedEvent NewOrderEvent(Guid? eventId = null, Guid? orderId = null, int total = 2000)
    {
        return new OrderCreatedEvent(
            EventId: eventId ?? Guid.NewGuid(),
            EventType: RoutingKeys.OrderCreated,
            OccurredAt: new DateTimeOffset(2026, 5, 16, 11, 59, 0, TimeSpan.Zero),
            OrderId: orderId ?? Guid.NewGuid(),
            CustomerId: "cust-0042",
            Items: new[]
            {
                new OrderEventItem("item-burger", "Cheeseburger", 2, 850),
                new OrderEventItem("item-fries",  "Fries",        1, 300)
            },
            TotalAmountCents: total,
            Currency: "USD");
    }

    [Fact]
    public async Task Happy_path_persists_succeeded_record_and_publishes_payment_succeeded()
    {
        var handler = NewHandler(out var db);
        var inbound = NewOrderEvent();

        var result = await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.Status.Should().Be(PaymentStatus.Succeeded);

        var record = db.PaymentRecords.Single();
        record.Id.Should().Be(result.PaymentId);
        record.OrderId.Should().Be(inbound.OrderId);
        record.Status.Should().Be(PaymentStatus.Succeeded);
        record.AmountChargedCents.Should().Be(2000);
        record.Currency.Should().Be("USD");
        record.AttemptNumber.Should().Be(1);
        record.FailureReason.Should().BeNull();
        record.CreatedAt.Should().Be(_clock.GetUtcNow());

        _publisher.Published.Should().HaveCount(1);
        var pub = _publisher.Published[0];
        pub.RoutingKey.Should().Be(RoutingKeys.PaymentSucceeded);
        pub.MessageId.Should().Be(result.OutboundEventId);
        var payload = pub.Payload.Should().BeOfType<PaymentSucceededEvent>().Subject;
        payload.EventType.Should().Be(RoutingKeys.PaymentSucceeded);
        payload.OrderId.Should().Be(inbound.OrderId);
        payload.PaymentId.Should().Be(record.Id.ToString());
        payload.AmountChargedCents.Should().Be(2000);
        payload.Currency.Should().Be("USD");
        payload.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public async Task Inserts_processed_event_id_on_success()
    {
        var handler = NewHandler(out var db);
        var inbound = NewOrderEvent();

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
        var inbound = NewOrderEvent();

        await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);
        _publisher.Published.Clear();
        _simulator.CallCount = 0;

        var second = await handler.HandleAsync(inbound, attemptNumber: 1, CancellationToken.None);

        second.Skipped.Should().BeTrue();
        second.Status.Should().Be("SKIPPED");
        _publisher.Published.Should().BeEmpty();
        _simulator.CallCount.Should().Be(0);
        db.PaymentRecords.Count().Should().Be(1);
        db.ProcessedEventIds.Count().Should().Be(1);

        // DOG-51: both invocations record a metric row — first SUCCESS, second SKIPPED_DUPLICATE.
        _metrics.Written.Should().HaveCount(2);
        _metrics.Written[0].ServiceName.Should().Be("payment");
        _metrics.Written[0].Outcome.Should().Be(MetricOutcomes.Success);
        _metrics.Written[1].Outcome.Should().Be(MetricOutcomes.SkippedDuplicate);
        _metrics.Written[1].OrderId.Should().Be(inbound.OrderId);
        _metrics.Written[1].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task Failed_path_persists_failed_record_and_publishes_payment_failed()
    {
        _simulator.ShouldSucceed = false;
        _simulator.FailureReason = PaymentFailureReason.PaymentDeclined;
        var handler = NewHandler(out var db);
        var inbound = NewOrderEvent();

        var result = await handler.HandleAsync(inbound, attemptNumber: 2, CancellationToken.None);

        result.Status.Should().Be(PaymentStatus.Failed);

        var record = db.PaymentRecords.Single();
        record.Status.Should().Be(PaymentStatus.Failed);
        record.AmountChargedCents.Should().Be(0);
        record.FailureReason.Should().Be(PaymentFailureReason.PaymentDeclined);
        record.AttemptNumber.Should().Be(2);

        _publisher.Published.Should().HaveCount(1);
        var pub = _publisher.Published[0];
        pub.RoutingKey.Should().Be(RoutingKeys.PaymentFailed);
        var payload = pub.Payload.Should().BeOfType<PaymentFailedEvent>().Subject;
        payload.OrderId.Should().Be(inbound.OrderId);
        payload.Reason.Should().Be(PaymentFailureReason.PaymentDeclined);
        payload.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task Outbound_event_ids_are_unique_per_publish()
    {
        var handler = NewHandler(out _);

        var r1 = await handler.HandleAsync(NewOrderEvent(), 1, CancellationToken.None);
        var r2 = await handler.HandleAsync(NewOrderEvent(), 1, CancellationToken.None);

        r1.OutboundEventId.Should().NotBe(Guid.Empty);
        r2.OutboundEventId.Should().NotBe(Guid.Empty);
        r1.OutboundEventId.Should().NotBe(r2.OutboundEventId);
        r1.PaymentId.Should().NotBe(r2.PaymentId);
    }

    [Fact]
    public async Task Empty_event_id_is_rejected()
    {
        var handler = NewHandler(out _);
        var inbound = NewOrderEvent(eventId: Guid.Empty);

        var act = () => handler.HandleAsync(inbound, 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*empty eventId*");
    }

    [Fact]
    public void ReadAttemptNumber_returns_1_when_no_header()
    {
        OrderCreatedHandler.ReadAttemptNumber(null).Should().Be(1);
    }
}
