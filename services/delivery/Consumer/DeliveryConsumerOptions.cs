namespace DeliveryService.Consumer;

public sealed class DeliveryConsumerOptions
{
    public const string SectionName = "DeliveryConsumer";

    public string QueueName { get; set; } = "delivery.queue";
    public string DeadLetterQueue { get; set; } = "delivery.dlq";
    public string ConsumesRoutingKey { get; set; } = "order.ready";

    /// <summary>Prefix for the per-attempt retry queues (<c>&lt;prefix&gt;.retry.{1,2,3}</c>).</summary>
    public string RetryQueuePrefix { get; set; } = "delivery";

    public ushort PrefetchCount { get; set; } = 10;
}
