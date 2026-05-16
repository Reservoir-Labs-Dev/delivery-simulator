using Microsoft.EntityFrameworkCore;
using OrderService.Data.Models;

namespace OrderService.Data;

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<ProcessedEventId> ProcessedEventIds => Set<ProcessedEventId>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
            b.Property(e => e.Status).HasColumnName("status").IsRequired();
            b.Property(e => e.TotalAmountCents).HasColumnName("total_amount_cents");
            b.Property(e => e.Currency).HasColumnName("currency").HasDefaultValue("USD");
            b.Property(e => e.CreatedAt).HasColumnName("created_at");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            b.HasMany(e => e.Items)
                .WithOne()
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(e => e.CustomerId);
            b.HasIndex(e => e.Status);
            b.HasIndex(e => e.CreatedAt).IsDescending();
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.ToTable("order_items");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.OrderId).HasColumnName("order_id");
            b.Property(e => e.ItemId).HasColumnName("item_id").IsRequired();
            b.Property(e => e.Name).HasColumnName("name").IsRequired();
            b.Property(e => e.Quantity).HasColumnName("quantity");
            b.Property(e => e.UnitPriceCents).HasColumnName("unit_price_cents");

            b.HasIndex(e => e.OrderId);
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
