namespace KitchenService.Data.Models;

public class KitchenOrder
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset PrepStartedAt { get; set; }
    public DateTimeOffset? PrepReadyAt { get; set; }
    public int? PrepDurationMs { get; set; }
}
