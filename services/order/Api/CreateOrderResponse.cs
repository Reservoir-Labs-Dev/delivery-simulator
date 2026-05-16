namespace OrderService.Api;

public sealed record CreateOrderResponse(
    Guid OrderId,
    string Status,
    int TotalAmountCents,
    string Currency,
    DateTimeOffset CreatedAt,
    Guid EventId);
