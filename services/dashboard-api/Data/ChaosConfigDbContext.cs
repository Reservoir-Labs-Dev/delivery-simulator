using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Persistence;

namespace DashboardApi.Data;

/// <summary>
/// Owns the shared <c>chaos.chaos_config</c> table (DOG-43). The Dashboard
/// writes; M3 consumer services will read the same table via their own
/// DbContext over the identical schema.
/// </summary>
public class ChaosConfigDbContext : DbContext
{
    public ChaosConfigDbContext(DbContextOptions<ChaosConfigDbContext> options) : base(options) { }

    public DbSet<ChaosConfig> ChaosConfigs => Set<ChaosConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("chaos");

        modelBuilder.Entity<ChaosConfig>(b =>
        {
            b.ToTable("chaos_config");
            b.HasKey(e => e.Name);
            b.Property(e => e.Name).HasColumnName("name").HasMaxLength(128);
            b.Property(e => e.Enabled).HasColumnName("enabled");
            b.Property(e => e.Params).HasColumnName("params").HasColumnType("jsonb").HasDefaultValue("{}");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
