using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Api;
using OrderService.Domain;
using OrderService.Events;
using OrderService.Handlers;
using OrderService.Tests.TestSupport;

namespace OrderService.Tests;

public class CreateOrderHandlerTests
{
    private readonly FakeEventPublisher _publisher = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

    private CreateOrderHandler NewHandler(out OrderService.Data.OrdersDbContext db)
    {
        db = InMemoryDb.Create();
        return new CreateOrderHandler(db, _publisher, _clock, NullLogger<CreateOrderHandler>.Instance);
    }

    private static CreateOrderRequest ValidRequest() => new(
        CustomerId: "cust-0042",
        Currency: "USD",
        Items: new[]
        {
            new CreateOrderItemRequest("item-burger", "Cheeseburger", 2, 850),
            new CreateOrderItemRequest("item-fries",  "Fries",        1, 300)
        });

    [Fact]
    public async Task Persists_order_with_CREATED_status_and_computed_total()
    {
        var handler = NewHandler(out var db);

        var response = await handler.HandleAsync(ValidRequest(), CancellationToken.None);

        var persisted = db.Orders.Single();
        persisted.Id.Should().Be(response.OrderId);
        persisted.Status.Should().Be(OrderStatus.Created);
        persisted.TotalAmountCents.Should().Be(2 * 850 + 1 * 300);
        persisted.Currency.Should().Be("USD");
        persisted.Items.Should().HaveCount(2);
        persisted.Items.Select(i => i.ItemId).Should().BeEquivalentTo(new[] { "item-burger", "item-fries" });
        persisted.CreatedAt.Should().Be(_clock.GetUtcNow());
        persisted.UpdatedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Publishes_order_created_with_correct_routing_key_and_payload()
    {
        var handler = NewHandler(out _);

        var response = await handler.HandleAsync(ValidRequest(), CancellationToken.None);

        _publisher.Published.Should().HaveCount(1);
        var published = _publisher.Published[0];

        published.RoutingKey.Should().Be("order.created");
        published.MessageId.Should().Be(response.EventId);
        published.OccurredAt.Should().Be(_clock.GetUtcNow());

        var evt = published.Payload.Should().BeOfType<OrderCreatedEvent>().Subject;
        evt.EventId.Should().Be(response.EventId);
        evt.EventId.Should().NotBe(Guid.Empty);
        evt.EventType.Should().Be("order.created");
        evt.OrderId.Should().Be(response.OrderId);
        evt.CustomerId.Should().Be("cust-0042");
        evt.TotalAmountCents.Should().Be(2000);
        evt.Currency.Should().Be("USD");
        evt.Items.Should().HaveCount(2);
        evt.Items[0].Should().Be(new OrderEventItem("item-burger", "Cheeseburger", 2, 850));
        evt.Items[1].Should().Be(new OrderEventItem("item-fries",  "Fries",        1, 300));
    }

    [Fact]
    public async Task Idempotency_key_in_event_is_unique_per_call()
    {
        var handler = NewHandler(out _);

        var r1 = await handler.HandleAsync(ValidRequest(), CancellationToken.None);
        var r2 = await handler.HandleAsync(ValidRequest(), CancellationToken.None);

        r1.EventId.Should().NotBe(Guid.Empty);
        r2.EventId.Should().NotBe(Guid.Empty);
        r1.EventId.Should().NotBe(r2.EventId);

        _publisher.Published.Select(p => p.MessageId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Returns_201_response_payload_matching_persisted_order()
    {
        var handler = NewHandler(out _);

        var response = await handler.HandleAsync(ValidRequest(), CancellationToken.None);

        response.Status.Should().Be(OrderStatus.Created);
        response.TotalAmountCents.Should().Be(2000);
        response.Currency.Should().Be("USD");
        response.CreatedAt.Should().Be(_clock.GetUtcNow());
        response.OrderId.Should().NotBe(Guid.Empty);
        response.EventId.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("",       false, "customerId")]
    [InlineData("cust-1", true,  "item")]
    public async Task Rejects_invalid_request(string customerId, bool emptyItems, string expectedFragment)
    {
        var handler = NewHandler(out _);

        var request = new CreateOrderRequest(
            CustomerId: customerId,
            Currency: "USD",
            Items: emptyItems
                ? Array.Empty<CreateOrderItemRequest>()
                : new[] { new CreateOrderItemRequest("item-x", "X", 1, 100) });

        var act = () => handler.HandleAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.Message.Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task Rejects_zero_quantity_item()
    {
        var handler = NewHandler(out _);

        var request = new CreateOrderRequest(
            CustomerId: "cust-1",
            Currency: "USD",
            Items: new[] { new CreateOrderItemRequest("item-x", "X", 0, 100) });

        await FluentActions
            .Awaiting(() => handler.HandleAsync(request, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>()
            .Where(e => e.Message.Contains("Quantity", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}
