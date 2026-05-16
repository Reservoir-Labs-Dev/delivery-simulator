namespace KitchenService.Consumer;

public sealed class KitchenConsumerOptions
{
    public const string SectionName = "KitchenConsumer";

    public string QueueName { get; set; } = "kitchen.queue";
    public string DeadLetterQueue { get; set; } = "kitchen.dlq";
    public string ConsumesRoutingKey { get; set; } = "payment.succeeded";
    public ushort PrefetchCount { get; set; } = 10;
}
