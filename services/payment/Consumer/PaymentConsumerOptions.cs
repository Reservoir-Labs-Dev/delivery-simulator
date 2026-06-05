namespace PaymentService.Consumer;

public sealed class PaymentConsumerOptions
{
    public const string SectionName = "PaymentConsumer";

    public string QueueName { get; set; } = "payment.queue";
    public string DeadLetterQueue { get; set; } = "payment.dlq";
    public string ConsumesRoutingKey { get; set; } = "order.created";

    /// <summary>Prefix for the per-attempt retry queues (<c>&lt;prefix&gt;.retry.{1,2,3}</c>).</summary>
    public string RetryQueuePrefix { get; set; } = "payment";

    public ushort PrefetchCount { get; set; } = 10;
}
