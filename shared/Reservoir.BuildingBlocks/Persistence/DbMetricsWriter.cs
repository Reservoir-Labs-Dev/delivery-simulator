using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// Postgres-backed <see cref="IMetricsWriter"/> that rents a fresh
/// <see cref="MetricsDbContext"/> per call via
/// <see cref="IDbContextFactory{TContext}"/>. Safe to register as a
/// singleton: no shared per-message state, and the factory hands out a new
/// context+connection for each write.
///
/// Errors are caught and logged at Warning level. A metrics write must
/// never take down a working pipeline — observability is best-effort.
/// </summary>
public sealed class DbMetricsWriter : IMetricsWriter
{
    private readonly IDbContextFactory<MetricsDbContext> _factory;
    private readonly ILogger<DbMetricsWriter> _logger;

    public DbMetricsWriter(IDbContextFactory<MetricsDbContext> factory, ILogger<DbMetricsWriter> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task WriteAsync(
        Guid orderId,
        string serviceName,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int retryCount,
        string outcome,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            db.Metrics.Add(new Metric
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                ServiceName = serviceName,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                RetryCount = retryCount,
                Outcome = outcome,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to write metric row for order {OrderId} (service={Service}, outcome={Outcome})",
                orderId, serviceName, outcome);
        }
    }
}
