namespace Reservoir.BuildingBlocks.Contracts;

public sealed record DeliveryFailedEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string Reason,
    int AttemptNumber,
    bool RetryExhausted,
    string Outcome = EventOutcome.Failed);
