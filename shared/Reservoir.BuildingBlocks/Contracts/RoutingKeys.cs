namespace Reservoir.BuildingBlocks.Contracts;

public static class RoutingKeys
{
    public const string OrderCreated      = "order.created";
    public const string PaymentSucceeded  = "payment.succeeded";
    public const string PaymentFailed     = "payment.failed";
    public const string OrderReady        = "order.ready";
    public const string DeliveryCompleted = "delivery.completed";
    public const string DeliveryFailed    = "delivery.failed";
}
