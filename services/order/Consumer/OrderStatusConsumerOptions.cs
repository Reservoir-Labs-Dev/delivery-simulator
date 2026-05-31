using Reservoir.BuildingBlocks.Contracts;

namespace OrderService.Consumer;

public sealed class OrderStatusConsumerOptions
{
    public const string SectionName = "OrderStatusConsumer";

    public string QueueName { get; set; } = "order.status.queue";
    public string DeadLetterQueue { get; set; } = "order.status.dlq";
    public ushort PrefetchCount { get; set; } = 20;

    /// <summary>
    /// Status-bearing routing keys this consumer binds to on
    /// <c>orders.exchange</c>. <c>order.created</c> is intentionally excluded —
    /// OrderService publishes it, the row already exists in <c>CREATED</c>.
    /// </summary>
    public IReadOnlyList<string> SubscribesTo { get; set; } = new[]
    {
        RoutingKeys.PaymentSucceeded,
        RoutingKeys.PaymentFailed,
        RoutingKeys.OrderReady,
        RoutingKeys.DeliveryCompleted,
        RoutingKeys.DeliveryFailed,
    };
}
