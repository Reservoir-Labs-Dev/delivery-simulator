using DeliveryService.Data.Models;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Persistence;

namespace DeliveryService.Data;

public class DeliveryDbContext : DbContext
{
    public DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : base(options) { }

    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<ProcessedEventId> ProcessedEventIds => Set<ProcessedEventId>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("delivery");

        modelBuilder.Entity<Delivery>(b =>
        {
            b.ToTable("deliveries");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.OrderId).HasColumnName("order_id");
            b.Property(e => e.Status).HasColumnName("status").IsRequired();
            b.Property(e => e.AttemptNumber).HasColumnName("attempt_number").HasDefaultValue(1);
            b.Property(e => e.FailureReason).HasColumnName("failure_reason");
            b.Property(e => e.StartedAt).HasColumnName("started_at");
            b.Property(e => e.CompletedAt).HasColumnName("completed_at");

            b.HasIndex(e => e.OrderId);
            b.HasIndex(e => e.Status);
            b.HasIndex(e => e.StartedAt).IsDescending();
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
