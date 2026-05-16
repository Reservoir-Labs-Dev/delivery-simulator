namespace Reservoir.BuildingBlocks.Contracts;

public sealed record PaymentSucceededEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string PaymentId,
    int AmountChargedCents,
    string Currency,
    int AttemptNumber);
