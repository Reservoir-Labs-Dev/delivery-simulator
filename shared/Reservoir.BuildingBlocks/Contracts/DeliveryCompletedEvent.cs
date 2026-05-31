namespace Reservoir.BuildingBlocks.Contracts;

public sealed record DeliveryCompletedEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string DeliveryId,
    DateTimeOffset DeliveredAt,
    int AttemptNumber,
    string Outcome = EventOutcome.Success);
