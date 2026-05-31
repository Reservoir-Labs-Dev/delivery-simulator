using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;
using Reservoir.BuildingBlocks.Serialization;

namespace OrderService.Handlers;

public sealed class OrderStatusEventHandler
{
    private readonly OrdersDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrderStatusEventHandler> _logger;

    public OrderStatusEventHandler(
        OrdersDbContext db,
        TimeProvider clock,
        ILogger<OrderStatusEventHandler> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public Task<HandleResult> HandleAsync(string routingKey, byte[] body, CancellationToken ct)
    {
        var parsed = Parse(routingKey, body);
        return HandleAsync(routingKey, parsed.EventId, parsed.OrderId, parsed.NewStatus, ct);
    }

    public async Task<HandleResult> HandleAsync(
        string routingKey,
        Guid eventId,
        Guid orderId,
        string newStatus,
        CancellationToken ct)
    {
        if (eventId == Guid.Empty)
            throw new InvalidOperationException($"Event on '{routingKey}' has empty eventId");

        var alreadyProcessed = await _db.ProcessedEventIds
            .AsNoTracking()
            .AnyAsync(e => e.EventId == eventId, ct);

        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "Skipping duplicate event {EventId} on {RoutingKey} for order {OrderId}",
                eventId, routingKey, orderId);
            return HandleResult.ForSkipped(eventId);
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        var now = _clock.GetUtcNow();

        if (order is null)
        {
            // The status event arrived before we know the order. Don't crash the
            // consumer (would DLX an otherwise valid message); record the eventId
            // so a redelivery doesn't double-act once the row exists.
            _logger.LogWarning(
                "Received {RoutingKey} for unknown orderId={OrderId}; recording eventId {EventId} and skipping update",
                routingKey, orderId, eventId);

            _db.ProcessedEventIds.Add(new ProcessedEventId { EventId = eventId, ProcessedAt = now });
            await _db.SaveChangesAsync(ct);
            return new HandleResult(eventId, orderId, PreviousStatus: null, NewStatus: null, Skipped: false);
        }

        var previousStatus = order.Status;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        order.Status = newStatus;
        order.UpdatedAt = now;
        _db.ProcessedEventIds.Add(new ProcessedEventId { EventId = eventId, ProcessedAt = now });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} status {Previous} -> {Next} (eventId={EventId}, routingKey={RoutingKey})",
            orderId, previousStatus, newStatus, eventId, routingKey);

        return new HandleResult(eventId, orderId, previousStatus, newStatus, Skipped: false);
    }

    internal static ParsedStatusEvent Parse(string routingKey, byte[] body) => routingKey switch
    {
        RoutingKeys.PaymentSucceeded  => From<PaymentSucceededEvent> (body, OrderStatus.PaymentSucceeded, e => (e.EventId, e.OrderId)),
        RoutingKeys.PaymentFailed     => From<PaymentFailedEvent>    (body, OrderStatus.PaymentFailed,    e => (e.EventId, e.OrderId)),
        RoutingKeys.OrderReady        => From<OrderReadyEvent>       (body, OrderStatus.OrderReady,       e => (e.EventId, e.OrderId)),
        RoutingKeys.DeliveryCompleted => From<DeliveryCompletedEvent>(body, OrderStatus.Delivered,        e => (e.EventId, e.OrderId)),
        RoutingKeys.DeliveryFailed    => From<DeliveryFailedEvent>   (body, OrderStatus.DeliveryFailed,   e => (e.EventId, e.OrderId)),
        _ => throw new InvalidOperationException($"No status mapping for routing key '{routingKey}'"),
    };

    private static ParsedStatusEvent From<T>(byte[] body, string newStatus, Func<T, (Guid EventId, Guid OrderId)> extract)
        where T : class
    {
        var evt = JsonSerializer.Deserialize<T>(body, EventJsonOptions.Web)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name} (null).");
        var (eventId, orderId) = extract(evt);
        return new ParsedStatusEvent(eventId, orderId, newStatus);
    }

    internal readonly record struct ParsedStatusEvent(Guid EventId, Guid OrderId, string NewStatus);
}

public sealed record HandleResult(
    Guid EventId,
    Guid OrderId,
    string? PreviousStatus,
    string? NewStatus,
    bool Skipped)
{
    public static HandleResult ForSkipped(Guid eventId) =>
        new(eventId, Guid.Empty, null, null, Skipped: true);
}
