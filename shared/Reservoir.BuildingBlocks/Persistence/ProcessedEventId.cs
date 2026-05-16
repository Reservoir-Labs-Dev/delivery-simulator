namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// Idempotency record. Each consumer service owns its own table mapped from this
/// POCO; the EF mapping (schema/column names) is configured per service.
/// </summary>
public class ProcessedEventId
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
