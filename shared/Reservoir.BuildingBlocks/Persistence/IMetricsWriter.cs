namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// Writes one <see cref="Metric"/> row per message-handler attempt. Called
/// from every consumer handler (payment, kitchen, delivery) on every exit
/// path: success, skipped-duplicate, and failed. Implementations must
/// swallow their own infrastructure errors — a metrics write must never
/// take down a working pipeline.
/// </summary>
public interface IMetricsWriter
{
    Task WriteAsync(
        Guid orderId,
        string serviceName,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int retryCount,
        string outcome,
        CancellationToken ct);
}
