namespace OrderService.Data.Models;

public class ProcessedEventId
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
