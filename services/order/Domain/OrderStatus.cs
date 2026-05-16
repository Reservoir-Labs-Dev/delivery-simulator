namespace OrderService.Domain;

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
