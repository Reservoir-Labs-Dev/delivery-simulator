namespace DeliveryService.Data.Models;

public class Delivery
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AttemptNumber { get; set; } = 1;
    public string? FailureReason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
