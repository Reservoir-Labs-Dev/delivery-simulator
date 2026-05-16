namespace Reservoir.BuildingBlocks.Contracts;

public sealed record OrderCreatedEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    string CustomerId,
    IReadOnlyList<OrderEventItem> Items,
    int TotalAmountCents,
    string Currency);

public sealed record OrderEventItem(
    string ItemId,
    string Name,
    int Quantity,
    int UnitPriceCents);
