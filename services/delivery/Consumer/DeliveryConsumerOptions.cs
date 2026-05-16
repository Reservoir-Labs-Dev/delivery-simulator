namespace DeliveryService.Consumer;

public sealed class DeliveryConsumerOptions
{
    public const string SectionName = "DeliveryConsumer";

    public string QueueName { get; set; } = "delivery.queue";
    public string DeadLetterQueue { get; set; } = "delivery.dlq";
    public string ConsumesRoutingKey { get; set; } = "order.ready";
    public ushort PrefetchCount { get; set; } = 10;
}
