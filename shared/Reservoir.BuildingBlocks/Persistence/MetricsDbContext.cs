using Microsoft.EntityFrameworkCore;

namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// EF Core mapping for the shared <c>metrics.metrics</c> table introduced in
/// DOG-51. Every consumer service (payment, kitchen, delivery) writes here
/// via <see cref="IMetricsWriter"/>; dashboard-api reads it for the
/// <c>GET /metrics/export.csv</c> endpoint that M4 experiments consume.
/// </summary>
public class MetricsDbContext : DbContext
{
    public MetricsDbContext(DbContextOptions<MetricsDbContext> options) : base(options) { }

    public DbSet<Metric> Metrics => Set<Metric>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("metrics");

        modelBuilder.Entity<Metric>(b =>
        {
            b.ToTable("metrics");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id");
            b.Property(e => e.OrderId).HasColumnName("order_id");
            b.Property(e => e.ServiceName).HasColumnName("service_name").HasMaxLength(32);
            b.Property(e => e.StartedAt).HasColumnName("started_at");
            b.Property(e => e.CompletedAt).HasColumnName("completed_at");
            b.Property(e => e.RetryCount).HasColumnName("retry_count");
            b.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(32);

            b.HasIndex(e => e.OrderId).HasDatabaseName("ix_metrics_order_id");
            b.HasIndex(e => e.ServiceName).HasDatabaseName("ix_metrics_service_name");
            b.HasIndex(e => e.StartedAt).IsDescending().HasDatabaseName("ix_metrics_started_at");
        });
    }
}
