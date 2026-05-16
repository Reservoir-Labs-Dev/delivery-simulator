using System.Text.Json;
using KitchenService.Data;
using KitchenService.Data.Models;
using KitchenService.Domain;
using KitchenService.Simulation;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;
using Reservoir.BuildingBlocks.Serialization;

namespace KitchenService.Handlers;

public sealed class PaymentSucceededHandler
{
    public const string ConsumesRoutingKey = RoutingKeys.PaymentSucceeded;
    public const string PublishesRoutingKey = RoutingKeys.OrderReady;

    private readonly KitchenDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly IKitchenSimulator _simulator;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentSucceededHandler> _logger;

    public PaymentSucceededHandler(
        KitchenDbContext db,
        IEventPublisher publisher,
        IKitchenSimulator simulator,
        TimeProvider clock,
        ILogger<PaymentSucceededHandler> logger)
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
        return HandleAsync(evt, ct);
    }

    public async Task<HandleResult> HandleAsync(PaymentSucceededEvent evt, CancellationToken ct)
    {
        if (evt.EventId == Guid.Empty)
            throw new InvalidOperationException("PaymentSucceededEvent has empty eventId");

        var alreadyProcessed = await _db.ProcessedEventIds
            .AsNoTracking()
            .AnyAsync(e => e.EventId == evt.EventId, ct);

        if (alreadyProcessed)
        {
            _logger.LogInformation("Skipping duplicate event {EventId} for order {OrderId}", evt.EventId, evt.OrderId);
            return HandleResult.ForSkipped(evt.EventId);
        }

        // Simulate BEFORE opening the DB transaction — holding a transaction open
        // for ~500ms would needlessly block on a connection from the pool.
        var startedAt = _clock.GetUtcNow();
        var outcome = await _simulator.SimulateAsync(evt, ct);
        var readyAt = _clock.GetUtcNow();

        var kitchenOrderId = Guid.NewGuid();
        var outboundEventId = Guid.NewGuid();

        var record = new KitchenOrder
        {
            Id = kitchenOrderId,
            OrderId = evt.OrderId,
            Status = KitchenOrderStatus.Ready,
            PrepStartedAt = startedAt,
            PrepReadyAt = readyAt,
            PrepDurationMs = outcome.DurationMs,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.KitchenOrders.Add(record);
        _db.ProcessedEventIds.Add(new ProcessedEventId { EventId = evt.EventId, ProcessedAt = readyAt });
        await _db.SaveChangesAsync(ct);

        // M1 limitation: order.ready specifies an items list (ARCH-002 § 3.4) but
        // payment.succeeded doesn't carry items. Until items are propagated through
        // the pipeline (or kitchen subscribes to order.created as well), we emit
        // an empty list. Downstream consumers in M1 (DeliveryService, dashboard)
        // don't depend on items, so the gap is observable but not functional.
        var payload = new OrderReadyEvent(
            EventId: outboundEventId,
            EventType: PublishesRoutingKey,
            OccurredAt: readyAt,
            OrderId: evt.OrderId,
            PreparedAt: readyAt,
            PrepDurationMs: outcome.DurationMs,
            Items: Array.Empty<ReadyItem>());

        _publisher.Publish(PublishesRoutingKey, payload, outboundEventId, readyAt);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Prepared order {OrderId} in {Duration}ms (kitchenOrderId={KitchenOrderId}, eventId={EventId})",
            evt.OrderId, outcome.DurationMs, kitchenOrderId, outboundEventId);

        return new HandleResult(
            InboundEventId: evt.EventId,
            OutboundEventId: outboundEventId,
            KitchenOrderId: kitchenOrderId,
            PrepDurationMs: outcome.DurationMs,
            Skipped: false);
    }

    private static PaymentSucceededEvent DeserializePayload(byte[] body)
    {
        var evt = JsonSerializer.Deserialize<PaymentSucceededEvent>(body, EventJsonOptions.Web);
        if (evt is null)
            throw new InvalidOperationException("Failed to deserialize payment.succeeded payload (null).");
        return evt;
    }
}

public sealed record HandleResult(
    Guid InboundEventId,
    Guid OutboundEventId,
    Guid KitchenOrderId,
    int PrepDurationMs,
    bool Skipped)
{
    public static HandleResult ForSkipped(Guid inboundEventId) =>
        new(inboundEventId, Guid.Empty, Guid.Empty, 0, true);
}
