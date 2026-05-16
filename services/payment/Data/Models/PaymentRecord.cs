namespace PaymentService.Data.Models;

public class PaymentRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AmountChargedCents { get; set; }
    public string Currency { get; set; } = "USD";
    public int AttemptNumber { get; set; } = 1;
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
