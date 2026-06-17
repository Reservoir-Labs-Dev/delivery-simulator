using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Reservoir.BuildingBlocks.Messaging;

/// <summary>
/// Recovery mechanism (DOG-134): drains a service's dead-letter queue and
/// republishes each message back onto the main exchange so a now-healed
/// consumer can reprocess it. This is the "recover" half of the resilience
/// story — DLQ routing (DOG-37) and bounded retry (DOG-38) <em>preserve</em>
/// undeliverable messages without loss; this replays them once the fault that
/// sent them there has been removed.
/// </summary>
/// <remarks>
/// <para><b>Zero-loss ordering.</b> Each message is taken from the DLQ with
/// <c>autoAck: false</c>, republished under publisher confirms, and only
/// <see cref="IModel.BasicAck"/>'d off the DLQ <em>after</em> the broker
/// confirms the republish. If the process dies or the republish is nacked
/// mid-drain, the un-acked DLQ message is requeued by the broker when the
/// channel closes — so a replayed message is never lost, only ever moved or
/// duplicated, and the idempotent consumers (DOG-39) absorb any duplicate.</para>
/// <para><b>Fresh retry budget.</b> The republished message preserves the
/// original <c>MessageId</c> (the idempotency key) but drops the
/// <see cref="ConsumerRetry.RetryCountHeader"/>, so the message re-enters the
/// pipeline with a full set of attempts rather than as an already-exhausted
/// delivery.</para>
/// </remarks>
public static class DlqReplayer
{
    /// <summary>
    /// Drains up to <paramref name="max"/> messages from <paramref name="dlqName"/> and
    /// republishes each to <paramref name="targetExchange"/> with
    /// <paramref name="targetRoutingKey"/>. Returns the number actually replayed
    /// (drain stops early when the DLQ is empty).
    /// </summary>
    public static int Drain(
        IModel channel,
        string dlqName,
        string targetExchange,
        string targetRoutingKey,
        ILogger? logger = null,
        int max = int.MaxValue)
    {
        channel.ConfirmSelect();

        var replayed = 0;
        while (replayed < max)
        {
            var msg = channel.BasicGet(dlqName, autoAck: false);
            if (msg is null)
                break; // DLQ drained.

            var props = channel.CreateBasicProperties();
            props.ContentType = msg.BasicProperties?.ContentType;
            props.ContentEncoding = msg.BasicProperties?.ContentEncoding;
            props.DeliveryMode = 2; // persistent
            props.MessageId = msg.BasicProperties?.MessageId; // idempotency key survives
            props.Type = msg.BasicProperties?.Type;
            // Intentionally omit ConsumerRetry.RetryCountHeader so the replay
            // re-enters with a fresh attempt budget.
            props.Headers = new Dictionary<string, object>();

            try
            {
                channel.BasicPublish(targetExchange, targetRoutingKey, basicProperties: props, body: msg.Body);
                channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Republish not confirmed: leave the message un-acked so the
                // broker requeues it onto the DLQ when the channel closes.
                channel.BasicNack(msg.DeliveryTag, multiple: false, requeue: true);
                throw;
            }

            channel.BasicAck(msg.DeliveryTag, multiple: false);
            replayed++;
        }

        logger?.LogInformation(
            "DLQ replay drained {Replayed} message(s) from {Dlq} → {Exchange}/{RoutingKey}",
            replayed, dlqName, targetExchange, targetRoutingKey);

        return replayed;
    }
}
