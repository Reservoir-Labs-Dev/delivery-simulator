using System.Text.Json;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Serialization;

namespace DashboardApi.Handlers;

/// <summary>
/// Translates inbound RabbitMQ events into <see cref="OrderStatusChangedNotification"/>
/// instances ready to broadcast over SignalR.
///
/// Knows about every status-bearing routing key. Returns <c>null</c> for routing
/// keys it doesn't recognise — the consumer treats that as "ack and ignore".
///
/// M1 note: the "in-flight" statuses from ARCH-002 § 4
/// (<c>PAYMENT_PROCESSING</c>, <c>KITCHEN_PREPARING</c>,
/// <c>DELIVERY_IN_PROGRESS</c>) are not produced — the bus only carries
/// completion events. See ADR-007 for the rationale.
/// </summary>
public sealed class StatusTranslator
{
    public OrderStatusChangedNotification? Translate(string routingKey, byte[] body)
    {
        return routingKey switch
        {
            RoutingKeys.OrderCreated      => FromOrderCreated(body),
            RoutingKeys.PaymentSucceeded  => FromPaymentSucceeded(body),
            RoutingKeys.PaymentFailed     => FromPaymentFailed(body),
            RoutingKeys.OrderReady        => FromOrderReady(body),
            RoutingKeys.DeliveryCompleted => FromDeliveryCompleted(body),
            RoutingKeys.DeliveryFailed    => FromDeliveryFailed(body),
            _ => null,
        };
    }

    private static OrderStatusChangedNotification FromOrderCreated(byte[] body)
    {
        var evt = Deserialize<OrderCreatedEvent>(body, nameof(OrderCreatedEvent));
        return new OrderStatusChangedNotification(
            OrderId: evt.OrderId,
            Status: OrderStatus.Created,
            UpdatedAt: evt.OccurredAt,
            SourceService: "OrderService",
            AttemptNumber: 1,
            RetryExhausted: false,
            Metadata: new Dictionary<string, object>
            {
                ["totalAmountCents"] = evt.TotalAmountCents,
                ["currency"] = evt.Currency,
                ["customerId"] = evt.CustomerId,
            });
    }

    private static OrderStatusChangedNotification FromPaymentSucceeded(byte[] body)
    {
        var evt = Deserialize<PaymentSucceededEvent>(body, nameof(PaymentSucceededEvent));
        return new OrderStatusChangedNotification(
            OrderId: evt.OrderId,
            Status: OrderStatus.PaymentSucceeded,
            UpdatedAt: evt.OccurredAt,
            SourceService: "PaymentService",
            AttemptNumber: evt.AttemptNumber,
            RetryExhausted: false,
            Metadata: new Dictionary<string, object>
            {
                ["paymentId"] = evt.PaymentId,
                ["amountChargedCents"] = evt.AmountChargedCents,
            });
    }

    private static OrderStatusChangedNotification FromPaymentFailed(byte[] body)
    {
        var evt = Deserialize<PaymentFailedEvent>(body, nameof(PaymentFailedEvent));
        return new OrderStatusChangedNotification(
            OrderId: evt.OrderId,
            Status: OrderStatus.PaymentFailed,
            UpdatedAt: evt.OccurredAt,
            SourceService: "PaymentService",
            AttemptNumber: evt.AttemptNumber,
            RetryExhausted: evt.RetryExhausted,
            Metadata: new Dictionary<string, object>
            {
                ["reason"] = evt.Reason,
            });
    }

    private static OrderStatusChangedNotification FromOrderReady(byte[] body)
    {
        var evt = Deserialize<OrderReadyEvent>(body, nameof(OrderReadyEvent));
        return new OrderStatusChangedNotification(
            OrderId: evt.OrderId,
            Status: OrderStatus.OrderReady,
            UpdatedAt: evt.OccurredAt,
            SourceService: "KitchenService",
            AttemptNumber: 1,
            RetryExhausted: false,
            Metadata: new Dictionary<string, object>
            {
                ["prepDurationMs"] = evt.PrepDurationMs,
            });
    }

    private static OrderStatusChangedNotification FromDeliveryCompleted(byte[] body)
    {
        var evt = Deserialize<DeliveryCompletedEvent>(body, nameof(DeliveryCompletedEvent));
        return new OrderStatusChangedNotification(
            OrderId: evt.OrderId,
            Status: OrderStatus.Delivered,
            UpdatedAt: evt.OccurredAt,
            SourceService: "DeliveryService",
            AttemptNumber: evt.AttemptNumber,
            RetryExhausted: false,
            Metadata: new Dictionary<string, object>
            {
                ["deliveryId"] = evt.DeliveryId,
            });
    }

    private static OrderStatusChangedNotification FromDeliveryFailed(byte[] body)
    {
        var evt = Deserialize<DeliveryFailedEvent>(body, nameof(DeliveryFailedEvent));
        return new OrderStatusChangedNotification(
            OrderId: evt.OrderId,
            Status: OrderStatus.DeliveryFailed,
            UpdatedAt: evt.OccurredAt,
            SourceService: "DeliveryService",
            AttemptNumber: evt.AttemptNumber,
            RetryExhausted: evt.RetryExhausted,
            Metadata: new Dictionary<string, object>
            {
                ["reason"] = evt.Reason,
            });
    }

    private static T Deserialize<T>(byte[] body, string typeNameForError) where T : class
    {
        var evt = JsonSerializer.Deserialize<T>(body, EventJsonOptions.Web);
        if (evt is null)
            throw new InvalidOperationException($"Failed to deserialize {typeNameForError} (null).");
        return evt;
    }
}
