using Microsoft.EntityFrameworkCore;

namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// EF Core mapping for the shared <c>chaos.chaos_config</c> table introduced
/// in DOG-43. The Dashboard owns writes (POST /chaos/set); M3 consumer
/// services attach this same DbContext to read their scenario row on every
/// message.
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
