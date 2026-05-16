using KitchenService.Data.Models;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Persistence;

namespace KitchenService.Data;

public class KitchenDbContext : DbContext
{
    public KitchenDbContext(DbContextOptions<KitchenDbContext> options) : base(options) { }

    public DbSet<KitchenOrder> KitchenOrders => Set<KitchenOrder>();
    public DbSet<ProcessedEventId> ProcessedEventIds => Set<ProcessedEventId>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("kitchen");

        modelBuilder.Entity<KitchenOrder>(b =>
        {
            b.ToTable("kitchen_orders");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.OrderId).HasColumnName("order_id");
            b.Property(e => e.Status).HasColumnName("status").IsRequired();
            b.Property(e => e.PrepStartedAt).HasColumnName("prep_started_at");
            b.Property(e => e.PrepReadyAt).HasColumnName("prep_ready_at");
            b.Property(e => e.PrepDurationMs).HasColumnName("prep_duration_ms");

            b.HasIndex(e => e.OrderId).IsUnique();
            b.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<ProcessedEventId>(b =>
        {
            b.ToTable("processed_event_ids");
            b.HasKey(e => e.EventId);
            b.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
            b.Property(e => e.ProcessedAt).HasColumnName("processed_at");
        });
    }
}
