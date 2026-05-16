using Reservoir.BuildingBlocks.Contracts;

namespace DashboardApi.Consumer;

public sealed class DashboardConsumerOptions
{
    public const string SectionName = "DashboardConsumer";

    public string QueueName { get; set; } = "dashboard.queue";
    public ushort PrefetchCount { get; set; } = 50;

    /// <summary>
    /// Routing keys to bind <see cref="QueueName"/> to on
    /// <c>orders.exchange</c>. The <see cref="StatusTranslator"/> must know
    /// every key listed here.
    /// </summary>
    public IReadOnlyList<string> SubscribesTo { get; set; } = new[]
    {
        RoutingKeys.OrderCreated,
        RoutingKeys.PaymentSucceeded,
        RoutingKeys.PaymentFailed,
        RoutingKeys.OrderReady,
        RoutingKeys.DeliveryCompleted,
        RoutingKeys.DeliveryFailed,
    };
}
