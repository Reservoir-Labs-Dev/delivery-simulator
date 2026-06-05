namespace KitchenService.Consumer;

public sealed class KitchenConsumerOptions
{
    public const string SectionName = "KitchenConsumer";

    public string QueueName { get; set; } = "kitchen.queue";
    public string DeadLetterQueue { get; set; } = "kitchen.dlq";
    public string ConsumesRoutingKey { get; set; } = "payment.succeeded";

    /// <summary>Prefix for the per-attempt retry queues (<c>&lt;prefix&gt;.retry.{1,2,3}</c>).</summary>
    public string RetryQueuePrefix { get; set; } = "kitchen";

    public ushort PrefetchCount { get; set; } = 10;
}
