using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Reservoir.BuildingBlocks.Persistence;

namespace DashboardApi.Tests;

public class DbMetricsWriterTests
{
    private static (DbMetricsWriter sut, IDbContextFactory<MetricsDbContext> factory) NewSut()
    {
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new InMemoryMetricsDbContextFactory(options);
        var sut = new DbMetricsWriter(factory, NullLogger<DbMetricsWriter>.Instance);
        return (sut, factory);
    }

    [Fact]
    public async Task WriteAsync_persists_a_single_row_with_all_fields()
    {
        var (sut, factory) = NewSut();
        var orderId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(123);

        await sut.WriteAsync(orderId, "payment", startedAt, completedAt, retryCount: 0, MetricOutcomes.Success, CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var row = db.Metrics.Single();
        row.Id.Should().NotBe(Guid.Empty);
        row.OrderId.Should().Be(orderId);
        row.ServiceName.Should().Be("payment");
        row.StartedAt.Should().Be(startedAt);
        row.CompletedAt.Should().Be(completedAt);
        row.RetryCount.Should().Be(0);
        row.Outcome.Should().Be(MetricOutcomes.Success);
    }

    [Fact]
    public async Task WriteAsync_assigns_a_unique_id_per_call()
    {
        var (sut, factory) = NewSut();
        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await sut.WriteAsync(orderId, "kitchen", now, now, 0, MetricOutcomes.Success, CancellationToken.None);
        await sut.WriteAsync(orderId, "kitchen", now, now, 1, MetricOutcomes.Failed, CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var rows = db.Metrics.ToList();
        rows.Should().HaveCount(2);
        rows[0].Id.Should().NotBe(rows[1].Id);
    }

    [Fact]
    public async Task WriteAsync_swallows_infrastructure_errors_to_protect_the_pipeline()
    {
        // A failing factory simulates Postgres being unreachable. The handler
        // contract is that metrics writes must never throw — observability is
        // best-effort. A bad metrics write must not take down message processing.
        var failingFactory = new ThrowingMetricsDbContextFactory();
        var sut = new DbMetricsWriter(failingFactory, NullLogger<DbMetricsWriter>.Instance);

        var act = () => sut.WriteAsync(
            Guid.NewGuid(), "delivery",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            0, MetricOutcomes.Failed, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed class InMemoryMetricsDbContextFactory : IDbContextFactory<MetricsDbContext>
    {
        private readonly DbContextOptions<MetricsDbContext> _options;
        public InMemoryMetricsDbContextFactory(DbContextOptions<MetricsDbContext> options) => _options = options;
        public MetricsDbContext CreateDbContext() => new(_options);
    }

    private sealed class ThrowingMetricsDbContextFactory : IDbContextFactory<MetricsDbContext>
    {
        public MetricsDbContext CreateDbContext() =>
            throw new InvalidOperationException("simulated DB outage");
    }
}
