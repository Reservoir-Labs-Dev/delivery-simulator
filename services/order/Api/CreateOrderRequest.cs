namespace OrderService.Api;

public sealed record CreateOrderRequest(
    string CustomerId,
    IReadOnlyList<CreateOrderItemRequest> Items,
    string Currency);

public sealed record CreateOrderItemRequest(
    string ItemId,
    string Name,
    int Quantity,
    int UnitPriceCents);
