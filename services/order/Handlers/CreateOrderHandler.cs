using OrderService.Api;
using OrderService.Data;
using OrderService.Data.Models;
using OrderService.Domain;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;

namespace OrderService.Handlers;

public sealed class CreateOrderHandler
{
    public const string RoutingKey = RoutingKeys.OrderCreated;

    private readonly OrdersDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly ILogger<CreateOrderHandler> _logger;

    public CreateOrderHandler(
        OrdersDbContext db,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger<CreateOrderHandler> logger)
    {
        _db = db;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CreateOrderResponse> HandleAsync(CreateOrderRequest request, CancellationToken ct)
    {
        Validate(request);

        var now = _clock.GetUtcNow();
        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var totalCents = request.Items.Sum(i => i.Quantity * i.UnitPriceCents);
        if (totalCents <= 0)
        {
            throw new ArgumentException("Order total must be greater than zero.", nameof(request));
        }

        var order = new Order
        {
            Id = orderId,
            CustomerId = request.CustomerId,
            Status = OrderStatus.Created,
            TotalAmountCents = totalCents,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency,
            CreatedAt = now,
            UpdatedAt = now,
            Items = request.Items.Select(i => new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                ItemId = i.ItemId,
                Name = i.Name,
                Quantity = i.Quantity,
                UnitPriceCents = i.UnitPriceCents
            }).ToList()
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        var evt = new OrderCreatedEvent(
            EventId: eventId,
            EventType: RoutingKey,
            OccurredAt: now,
            OrderId: orderId,
            CustomerId: order.CustomerId,
            Items: order.Items
                .Select(i => new OrderEventItem(i.ItemId, i.Name, i.Quantity, i.UnitPriceCents))
                .ToList(),
            TotalAmountCents: order.TotalAmountCents,
            Currency: order.Currency);

        _publisher.Publish(RoutingKey, evt, eventId, now);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} created (customer={CustomerId}, total={Total}{Currency}); published event {EventId}",
            orderId, order.CustomerId, totalCents, order.Currency, eventId);

        return new CreateOrderResponse(
            OrderId: orderId,
            Status: order.Status,
            TotalAmountCents: order.TotalAmountCents,
            Currency: order.Currency,
            CreatedAt: order.CreatedAt,
            EventId: eventId);
    }

    private static void Validate(CreateOrderRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.CustomerId))
            throw new ArgumentException("customerId is required.", nameof(request));
        if (request.Items is null || request.Items.Count == 0)
            throw new ArgumentException("At least one item is required.", nameof(request));

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemId))
                throw new ArgumentException("itemId is required for each item.", nameof(request));
            if (string.IsNullOrWhiteSpace(item.Name))
                throw new ArgumentException("name is required for each item.", nameof(request));
            if (item.Quantity < 1)
                throw new ArgumentException("Quantity must be >= 1 for each item.", nameof(request));
            if (item.UnitPriceCents < 0)
                throw new ArgumentException("UnitPriceCents must be >= 0 for each item.", nameof(request));
        }
    }
}
