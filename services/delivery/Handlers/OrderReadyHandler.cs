using System.Text.Json;
using DeliveryService.Data;
using DeliveryService.Data.Models;
using DeliveryService.Domain;
using DeliveryService.Simulation;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;
using Reservoir.BuildingBlocks.Serialization;

namespace DeliveryService.Handlers;

public sealed class OrderReadyHandler
{
    public const string ConsumesRoutingKey = RoutingKeys.OrderReady;
    public const string CompletedRoutingKey = RoutingKeys.DeliveryCompleted;
    public const string FailedRoutingKey = RoutingKeys.DeliveryFailed;

    private const string ServiceName = "delivery";

    private readonly DeliveryDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly IDeliverySimulator _simulator;
    private readonly TimeProvider _clock;
    private readonly IMetricsWriter _metrics;
    private readonly ILogger<OrderReadyHandler> _logger;

    public OrderReadyHandler(
        DeliveryDbContext db,
        IEventPublisher publisher,
        IDeliverySimulator simulator,
        TimeProvider clock,
        IMetricsWriter metrics,
        ILogger<OrderReadyHandler> logger)
    {
        _db = db;
        _publisher = publisher;
        _simulator = simulator;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    public Task<HandleResult> HandleAsync(byte[] body, IBasicProperties? props, CancellationToken ct)
    {
        var evt = DeserializePayload(body);
        var attemptNumber = ReadAttemptNumber(props);
        return HandleAsync(evt, attemptNumber, ct);
    }

    public async Task<HandleResult> HandleAsync(OrderReadyEvent evt, int attemptNumber, CancellationToken ct)
    {
        if (evt.EventId == Guid.Empty)
            throw new InvalidOperationException("OrderReadyEvent has empty eventId");

        var handlerStartedAt = _clock.GetUtcNow();
        var retryCount = attemptNumber - 1;

        try
        {
            var alreadyProcessed = await _db.ProcessedEventIds
                .AsNoTracking()
                .AnyAsync(e => e.EventId == evt.EventId, ct);

            if (alreadyProcessed)
            {
                _logger.LogInformation("Skipping duplicate event {EventId} for order {OrderId}", evt.EventId, evt.OrderId);
                await _metrics.WriteAsync(
                    evt.OrderId, ServiceName, handlerStartedAt, _clock.GetUtcNow(),
                    retryCount, MetricOutcomes.SkippedDuplicate, ct);
                return HandleResult.ForSkipped(evt.EventId);
            }

            // Simulate before opening the DB transaction — same reasoning as the
            // kitchen handler: don't hold a pooled connection across a 700ms sleep.
            var startedAt = _clock.GetUtcNow();
            var outcome = await _simulator.SimulateAsync(evt, ct);
            var completedAt = _clock.GetUtcNow();

            var deliveryId = Guid.NewGuid();
            var outboundEventId = Guid.NewGuid();

            var record = new Delivery
            {
                Id = deliveryId,
                OrderId = evt.OrderId,
                Status = outcome.Success ? DeliveryStatus.Completed : DeliveryStatus.Failed,
                AttemptNumber = attemptNumber,
                FailureReason = outcome.FailureReason,
                StartedAt = startedAt,
                CompletedAt = completedAt,
            };

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Deliveries.Add(record);
            _db.ProcessedEventIds.Add(new ProcessedEventId { EventId = evt.EventId, ProcessedAt = completedAt });
            await _db.SaveChangesAsync(ct);

            if (outcome.Success)
            {
                var payload = new DeliveryCompletedEvent(
                    EventId: outboundEventId,
                    EventType: CompletedRoutingKey,
                    OccurredAt: completedAt,
                    OrderId: evt.OrderId,
                    DeliveryId: deliveryId.ToString(),
                    DeliveredAt: completedAt,
                    AttemptNumber: attemptNumber,
                    Outcome: EventOutcome.Success);

                _publisher.Publish(CompletedRoutingKey, payload, outboundEventId, completedAt);
            }
            else
            {
                var payload = new DeliveryFailedEvent(
                    EventId: outboundEventId,
                    EventType: FailedRoutingKey,
                    OccurredAt: completedAt,
                    OrderId: evt.OrderId,
                    Reason: outcome.FailureReason ?? DeliveryFailureReason.DriverUnavailable,
                    AttemptNumber: attemptNumber,
                    RetryExhausted: false,
                    Outcome: EventOutcome.Failed);

                _publisher.Publish(FailedRoutingKey, payload, outboundEventId, completedAt);
            }

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Processed {InEventId} for order {OrderId} → {Outcome} (deliveryId={DeliveryId}, attempt={Attempt})",
                evt.EventId, evt.OrderId, outcome.Success ? "COMPLETED" : "FAILED", deliveryId, attemptNumber);

            await _metrics.WriteAsync(
                evt.OrderId, ServiceName, handlerStartedAt, _clock.GetUtcNow(),
                retryCount, MetricOutcomes.Success, ct);

            return new HandleResult(
                InboundEventId: evt.EventId,
                OutboundEventId: outboundEventId,
                DeliveryId: deliveryId,
                Status: record.Status,
                Skipped: false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await _metrics.WriteAsync(
                evt.OrderId, ServiceName, handlerStartedAt, _clock.GetUtcNow(),
                retryCount, MetricOutcomes.Failed, CancellationToken.None);
            throw;
        }
    }

    private static OrderReadyEvent DeserializePayload(byte[] body)
    {
        var evt = JsonSerializer.Deserialize<OrderReadyEvent>(body, EventJsonOptions.Web);
        if (evt is null)
            throw new InvalidOperationException("Failed to deserialize order.ready payload (null).");
        return evt;
    }

    internal static int ReadAttemptNumber(IBasicProperties? props) =>
        ConsumerRetry.ReadAttemptNumber(props);
}

public sealed record HandleResult(
    Guid InboundEventId,
    Guid OutboundEventId,
    Guid DeliveryId,
    string Status,
    bool Skipped)
{
    public static HandleResult ForSkipped(Guid inboundEventId) =>
        new(inboundEventId, Guid.Empty, Guid.Empty, "SKIPPED", true);
}
