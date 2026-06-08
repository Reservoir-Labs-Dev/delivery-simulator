using Reservoir.BuildingBlocks.Persistence;

namespace Reservoir.TestSupport;

/// <summary>
/// Captures every <see cref="IMetricsWriter.WriteAsync"/> call so handler unit
/// tests can assert that the SUCCESS / SKIPPED_DUPLICATE / FAILED metric row
/// was emitted for the right order with the right retry count.
/// </summary>
public sealed class FakeMetricsWriter : IMetricsWriter
{
    public List<WrittenMetric> Written { get; } = new();

    public Task WriteAsync(
        Guid orderId,
        string serviceName,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int retryCount,
        string outcome,
        CancellationToken ct)
    {
        Written.Add(new WrittenMetric(orderId, serviceName, startedAt, completedAt, retryCount, outcome));
        return Task.CompletedTask;
    }

    public sealed record WrittenMetric(
        Guid OrderId,
        string ServiceName,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        int RetryCount,
        string Outcome);
}
