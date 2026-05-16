namespace Reservoir.BuildingBlocks.Contracts;

/// <summary>
/// Canonical status values that flow through <see cref="OrderStatusChangedNotification"/>
/// and into the <c>orders.status</c> column. Matches the enum in ARCH-002 § 4.
///
/// These are pipeline-level UI statuses — distinct from each consumer service's
/// own row-status enums (PaymentStatus, KitchenOrderStatus, DeliveryStatus),
/// which describe the internal state of that service's persistence row.
/// </summary>
public static class OrderStatus
{
    public const string Created = "CREATED";
    public const string PaymentProcessing = "PAYMENT_PROCESSING";
    public const string PaymentSucceeded = "PAYMENT_SUCCEEDED";
    public const string PaymentFailed = "PAYMENT_FAILED";
    public const string KitchenPreparing = "KITCHEN_PREPARING";
    public const string OrderReady = "ORDER_READY";
    public const string DeliveryInProgress = "DELIVERY_IN_PROGRESS";
    public const string Delivered = "DELIVERED";
    public const string DeliveryFailed = "DELIVERY_FAILED";
    public const string DeadLettered = "DEAD_LETTERED";
}
