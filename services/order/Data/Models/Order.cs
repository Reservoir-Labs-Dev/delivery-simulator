namespace OrderService.Data.Models;

public class Order
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalAmountCents { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<OrderItem> Items { get; set; } = new();
}
