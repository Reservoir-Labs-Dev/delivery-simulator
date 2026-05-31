using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using PaymentService.Data.Models;
using PaymentService.Domain;
using PaymentService.Simulation;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;
using Reservoir.BuildingBlocks.Serialization;

namespace PaymentService.Handlers;

public sealed class OrderCreatedHandler
{
    public const string ConsumesRoutingKey = RoutingKeys.OrderCreated;
    public const string SucceededRoutingKey = RoutingKeys.PaymentSucceeded;
    public const string FailedRoutingKey = RoutingKeys.PaymentFailed;

    private readonly PaymentsDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly IPaymentSimulator _simulator;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrderCreatedHandler> _logger;

    public OrderCreatedHandler(
        PaymentsDbContext db,
        IEventPublisher publisher,
        IPaymentSimulator simulator,
        TimeProvider clock,
        ILogger<OrderCreatedHandler> logger)
    {
        _db = db;
        _publisher = publisher;
        _simulator = simulator;
        _clock = clock;
        _logger = logger;
    }

    public Task<HandleResult> HandleAsync(byte[] body, IBasicProperties? props, CancellationToken ct)
    {
        var evt = DeserializePayload(body);
        var attemptNumber = ReadAttemptNumber(props);
        return HandleAsync(evt, attemptNumber, ct);
    }

    public async Task<HandleResult> HandleAsync(OrderCreatedEvent evt, int attemptNumber, CancellationToken ct)
    {
        if (evt.EventId == Guid.Empty)
            throw new InvalidOperationException("OrderCreatedEvent has empty eventId");

        var alreadyProcessed = await _db.ProcessedEventIds
            .AsNoTracking()
            .AnyAsync(e => e.EventId == evt.EventId, ct);

        if (alreadyProcessed)
        {
            _logger.LogInformation("Skipping duplicate event {EventId} for order {OrderId}", evt.EventId, evt.OrderId);
            return HandleResult.ForSkipped(evt.EventId);
        }

        var outcome = await _simulator.SimulateAsync(evt, ct);
        var now = _clock.GetUtcNow();
        var paymentId = Guid.NewGuid();
        var outboundEventId = Guid.NewGuid();

        var record = new PaymentRecord
        {
            Id = paymentId,
            OrderId = evt.OrderId,
            Status = outcome.Success ? PaymentStatus.Succeeded : PaymentStatus.Failed,
            AmountChargedCents = outcome.Success ? evt.TotalAmountCents : 0,
            Currency = evt.Currency,
            AttemptNumber = attemptNumber,
            FailureReason = outcome.FailureReason,
            CreatedAt = now,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.PaymentRecords.Add(record);
        _db.ProcessedEventIds.Add(new ProcessedEventId { EventId = evt.EventId, ProcessedAt = now });
        await _db.SaveChangesAsync(ct);

        if (outcome.Success)
        {
            var payload = new PaymentSucceededEvent(
                EventId: outboundEventId,
                EventType: SucceededRoutingKey,
                OccurredAt: now,
                OrderId: evt.OrderId,
                PaymentId: paymentId.ToString(),
                AmountChargedCents: evt.TotalAmountCents,
                Currency: evt.Currency,
                AttemptNumber: attemptNumber,
                Outcome: EventOutcome.Success);

            _publisher.Publish(SucceededRoutingKey, payload, outboundEventId, now);
        }
        else
        {
            var payload = new PaymentFailedEvent(
                EventId: outboundEventId,
                EventType: FailedRoutingKey,
                OccurredAt: now,
                OrderId: evt.OrderId,
                Reason: outcome.FailureReason ?? PaymentFailureReason.PaymentDeclined,
                AttemptNumber: attemptNumber,
                RetryExhausted: false,
                Outcome: EventOutcome.Failed);

            _publisher.Publish(FailedRoutingKey, payload, outboundEventId, now);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Processed {InEventId} for order {OrderId} → {Outcome} (paymentId={PaymentId}, attempt={Attempt})",
            evt.EventId, evt.OrderId, outcome.Success ? "SUCCEEDED" : "FAILED", paymentId, attemptNumber);

        return new HandleResult(
            InboundEventId: evt.EventId,
            OutboundEventId: outboundEventId,
            PaymentId: paymentId,
            Status: record.Status,
            Skipped: false);
    }

    private static OrderCreatedEvent DeserializePayload(byte[] body)
    {
        var evt = JsonSerializer.Deserialize<OrderCreatedEvent>(body, EventJsonOptions.Web);
        if (evt is null)
            throw new InvalidOperationException("Failed to deserialize order.created payload (null).");
        return evt;
    }

    internal static int ReadAttemptNumber(IBasicProperties? props)
    {
        if (props?.Headers is null) return 1;
        if (!props.Headers.TryGetValue("x-death", out var raw)) return 1;
        if (raw is IList<object> deaths) return deaths.Count + 1;
        return 1;
    }
}

public sealed record HandleResult(
    Guid InboundEventId,
    Guid OutboundEventId,
    Guid PaymentId,
    string Status,
    bool Skipped)
{
    public static HandleResult ForSkipped(Guid inboundEventId) =>
        new(inboundEventId, Guid.Empty, Guid.Empty, "SKIPPED", true);
}
