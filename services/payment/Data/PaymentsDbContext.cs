using Microsoft.EntityFrameworkCore;
using PaymentService.Data.Models;
using Reservoir.BuildingBlocks.Persistence;

namespace PaymentService.Data;

public class PaymentsDbContext : DbContext
{
    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options) { }

    public DbSet<PaymentRecord> PaymentRecords => Set<PaymentRecord>();
    public DbSet<ProcessedEventId> ProcessedEventIds => Set<ProcessedEventId>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payments");

        modelBuilder.Entity<PaymentRecord>(b =>
        {
            b.ToTable("payment_records");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.OrderId).HasColumnName("order_id");
            b.Property(e => e.Status).HasColumnName("status").IsRequired();
            b.Property(e => e.AmountChargedCents).HasColumnName("amount_charged_cents");
            b.Property(e => e.Currency).HasColumnName("currency").HasDefaultValue("USD");
            b.Property(e => e.AttemptNumber).HasColumnName("attempt_number").HasDefaultValue(1);
            b.Property(e => e.FailureReason).HasColumnName("failure_reason");
            b.Property(e => e.CreatedAt).HasColumnName("created_at");

            b.HasIndex(e => e.OrderId);
            b.HasIndex(e => e.Status);
            b.HasIndex(e => e.CreatedAt).IsDescending();
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
