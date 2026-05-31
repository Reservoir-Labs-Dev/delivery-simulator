namespace Reservoir.BuildingBlocks.Contracts;

public sealed record OrderReadyEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    DateTimeOffset PreparedAt,
    int PrepDurationMs,
    IReadOnlyList<ReadyItem> Items,
    string Outcome = EventOutcome.Success);

public sealed record ReadyItem(string ItemId, string Name, int Quantity);
