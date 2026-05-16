namespace PaymentService.Consumer;

public sealed class PaymentConsumerOptions
{
    public const string SectionName = "PaymentConsumer";

    public string QueueName { get; set; } = "payment.queue";
    public string DeadLetterQueue { get; set; } = "payment.dlq";
    public string ConsumesRoutingKey { get; set; } = "order.created";
    public ushort PrefetchCount { get; set; } = 10;
}
