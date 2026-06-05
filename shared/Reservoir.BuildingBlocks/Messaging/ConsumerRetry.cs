using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Reservoir.BuildingBlocks.Messaging;

/// <summary>
/// Shared exponential-backoff retry logic for the pipeline consumers
/// (payment, kitchen, delivery). On a handler exception the consumer hands the
/// failed delivery to <see cref="HandleFailure"/>, which either republishes the
/// message to the next per-attempt retry queue (<c>&lt;prefix&gt;.retry.{n}</c>,
/// TTL 1s/2s/4s — see ARCH-003 §3.2) or, once the retries are exhausted, nacks
/// it to the service's dead-letter exchange so it lands in <c>&lt;prefix&gt;.dlq</c>.
///
/// The attempt count is tracked in an explicit <see cref="RetryCountHeader"/>
/// header that we set on each republish, rather than RabbitMQ's <c>x-death</c>
/// array. Because the retry mechanism acks-and-republishes (it does not nack the
/// primary queue), an explicit header is both deterministic and unit-testable
/// without a live broker, and keeps <c>attemptNumber</c> (= retries + 1)
/// consistent for the event payloads (DOG-40).
/// </summary>
public static class ConsumerRetry
{
    /// <summary>Custom header carrying the number of retries already performed (0 on first delivery).</summary>
    public const string RetryCountHeader = "x-retry-count";

    /// <summary>Maximum number of retries before a message is dead-lettered (1s, 2s, 4s).</summary>
    public const int MaxRetries = 3;

    /// <summary>Number of retries already performed for this delivery (0 if the header is absent).</summary>
    public static int ReadRetryCount(IBasicProperties? props) => ReadRetryCount(props?.Headers);

    /// <summary>Number of retries already performed, read from a raw header dictionary.</summary>
    public static int ReadRetryCount(IDictionary<string, object>? headers)
    {
        if (headers is null) return 0;
        if (!headers.TryGetValue(RetryCountHeader, out var raw)) return 0;
        return ToInt(raw);
    }

    /// <summary>1-based attempt number for the current delivery (first delivery = 1).</summary>
    public static int ReadAttemptNumber(IBasicProperties? props) => ReadRetryCount(props) + 1;

    /// <summary>1-based attempt number, read from a raw header dictionary.</summary>
    public static int ReadAttemptNumber(IDictionary<string, object>? headers) => ReadRetryCount(headers) + 1;

    /// <summary>
    /// Pure decision: given the retries already performed, decide whether to retry
    /// (and into which attempt) or to dead-letter. Kept side-effect-free so it can
    /// be unit-tested without a broker.
    /// </summary>
    public static RetryDecision Decide(int retryCount, int maxRetries = MaxRetries) =>
        retryCount >= maxRetries
            ? RetryDecision.DeadLetter()
            : RetryDecision.Retry(retryCount + 1);

    /// <summary>
    /// Handles a failed delivery on the consume channel: either republishes to the
    /// next retry queue (and acks the original) or nacks to the DLX when retries
    /// are exhausted. Must be called from the consumer's dispatch callback so the
    /// publish/ack are serialised on the same channel.
    /// </summary>
    public static void HandleFailure(
        IModel channel,
        BasicDeliverEventArgs ea,
        string retryQueuePrefix,
        ILogger logger,
        int maxRetries = MaxRetries)
    {
        var messageId = ea.BasicProperties?.MessageId ?? "(none)";
        var retryCount = ReadRetryCount(ea.BasicProperties);
        var decision = Decide(retryCount, maxRetries);

        if (!decision.ShouldRetry)
        {
            channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            logger.LogWarning(
                "Retries exhausted ({Count}/{Max}) for message {MessageId} on '{RoutingKey}'; dead-lettering to {Prefix}.dlq",
                retryCount, maxRetries, messageId, ea.RoutingKey, retryQueuePrefix);
            return;
        }

        var retryQueue = decision.RetryQueueName(retryQueuePrefix);
        var props = channel.CreateBasicProperties();
        props.ContentType = ea.BasicProperties?.ContentType;
        props.ContentEncoding = ea.BasicProperties?.ContentEncoding;
        props.DeliveryMode = 2;
        props.MessageId = ea.BasicProperties?.MessageId;
        props.Type = ea.BasicProperties?.Type;
        if (ea.BasicProperties is not null)
            props.Timestamp = ea.BasicProperties.Timestamp;
        props.Headers = new Dictionary<string, object> { [RetryCountHeader] = decision.NextRetryCount };

        // Publish to the retry queue by name via the default exchange. The retry
        // queue's TTL expires the message back onto orders.exchange with the
        // original routing key (ARCH-003 §3.2), redelivering it to the primary queue.
        channel.BasicPublish(exchange: string.Empty, routingKey: retryQueue, basicProperties: props, body: ea.Body);
        channel.BasicAck(ea.DeliveryTag, multiple: false);

        logger.LogWarning(
            "Scheduling retry {Next}/{Max} for message {MessageId} on '{RoutingKey}' via {RetryQueue}",
            decision.NextRetryCount, maxRetries, messageId, ea.RoutingKey, retryQueue);
    }

    private static int ToInt(object? raw) => raw switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        uint u => (int)u,
        ulong ul => (int)ul,
        byte[] bytes => int.TryParse(Encoding.UTF8.GetString(bytes), out var v) ? v : 0,
        string str => int.TryParse(str, out var v) ? v : 0,
        _ => 0,
    };
}

/// <summary>Outcome of <see cref="ConsumerRetry.Decide"/>: retry into attempt N, or dead-letter.</summary>
public readonly record struct RetryDecision(bool ShouldRetry, int NextRetryCount)
{
    public static RetryDecision Retry(int nextRetryCount) => new(true, nextRetryCount);
    public static RetryDecision DeadLetter() => new(false, 0);

    /// <summary>Name of the retry queue for this decision, e.g. <c>payment.retry.2</c>.</summary>
    public string RetryQueueName(string prefix) => $"{prefix}.retry.{NextRetryCount}";
}
