using System.Text.Json;
using DashboardApi.Handlers;
using FluentAssertions;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Serialization;
// System.Text.Json is used below in the Bytes() helper.

namespace DashboardApi.Tests;

public class StatusTranslatorTests
{
    private readonly StatusTranslator _sut = new();
    private static readonly DateTimeOffset Now = new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrderCreated_translates_to_CREATED_from_OrderService()
    {
        var orderId = Guid.NewGuid();
        var evt = new OrderCreatedEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.OrderCreated,
            OccurredAt: Now,
            OrderId: orderId,
            CustomerId: "cust-1",
            Items: new[] { new OrderEventItem("item-x", "X", 1, 100) },
            TotalAmountCents: 100,
            Currency: "USD");

        var result = _sut.Translate(RoutingKeys.OrderCreated, Bytes(evt))!;

        result.OrderId.Should().Be(orderId);
        result.Status.Should().Be(OrderStatus.Created);
        result.UpdatedAt.Should().Be(Now);
        result.SourceService.Should().Be("OrderService");
        result.AttemptNumber.Should().Be(1);
        result.RetryExhausted.Should().BeFalse();
        result.Metadata.Should().ContainKey("totalAmountCents").WhoseValue.Should().Be(100);
        result.Metadata.Should().ContainKey("currency").WhoseValue.Should().Be("USD");
        result.Metadata.Should().ContainKey("customerId").WhoseValue.Should().Be("cust-1");
    }

    [Fact]
    public void PaymentSucceeded_translates_to_PAYMENT_SUCCEEDED_from_PaymentService()
    {
        var orderId = Guid.NewGuid();
        var evt = new PaymentSucceededEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.PaymentSucceeded,
            OccurredAt: Now,
            OrderId: orderId,
            PaymentId: "pay-1",
            AmountChargedCents: 100,
            Currency: "USD",
            AttemptNumber: 2);

        var result = _sut.Translate(RoutingKeys.PaymentSucceeded, Bytes(evt))!;

        result.OrderId.Should().Be(orderId);
        result.Status.Should().Be(OrderStatus.PaymentSucceeded);
        result.SourceService.Should().Be("PaymentService");
        result.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public void PaymentFailed_translates_to_PAYMENT_FAILED_with_RetryExhausted()
    {
        var evt = new PaymentFailedEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.PaymentFailed,
            OccurredAt: Now,
            OrderId: Guid.NewGuid(),
            Reason: "PAYMENT_DECLINED",
            AttemptNumber: 3,
            RetryExhausted: true);

        var result = _sut.Translate(RoutingKeys.PaymentFailed, Bytes(evt))!;

        result.Status.Should().Be(OrderStatus.PaymentFailed);
        result.SourceService.Should().Be("PaymentService");
        result.RetryExhausted.Should().BeTrue();
        result.AttemptNumber.Should().Be(3);
    }

    [Fact]
    public void OrderReady_translates_to_ORDER_READY_from_KitchenService()
    {
        var evt = new OrderReadyEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.OrderReady,
            OccurredAt: Now,
            OrderId: Guid.NewGuid(),
            PreparedAt: Now,
            PrepDurationMs: 350,
            Items: Array.Empty<ReadyItem>());

        var result = _sut.Translate(RoutingKeys.OrderReady, Bytes(evt))!;

        result.Status.Should().Be(OrderStatus.OrderReady);
        result.SourceService.Should().Be("KitchenService");
    }

    [Fact]
    public void DeliveryCompleted_translates_to_DELIVERED_from_DeliveryService()
    {
        var evt = new DeliveryCompletedEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.DeliveryCompleted,
            OccurredAt: Now,
            OrderId: Guid.NewGuid(),
            DeliveryId: "del-1",
            DeliveredAt: Now,
            AttemptNumber: 1);

        var result = _sut.Translate(RoutingKeys.DeliveryCompleted, Bytes(evt))!;

        result.Status.Should().Be(OrderStatus.Delivered);
        result.SourceService.Should().Be("DeliveryService");
    }

    [Fact]
    public void DeliveryFailed_translates_to_DELIVERY_FAILED_with_reason()
    {
        var evt = new DeliveryFailedEvent(
            EventId: Guid.NewGuid(),
            EventType: RoutingKeys.DeliveryFailed,
            OccurredAt: Now,
            OrderId: Guid.NewGuid(),
            Reason: "DRIVER_UNAVAILABLE",
            AttemptNumber: 3,
            RetryExhausted: true);

        var result = _sut.Translate(RoutingKeys.DeliveryFailed, Bytes(evt))!;

        result.Status.Should().Be(OrderStatus.DeliveryFailed);
        result.SourceService.Should().Be("DeliveryService");
        result.RetryExhausted.Should().BeTrue();
    }

    [Fact]
    public void Unknown_routing_key_returns_null()
    {
        var result = _sut.Translate("foo.bar.baz", new byte[] { 0x7B, 0x7D });
        result.Should().BeNull();
    }

    private static byte[] Bytes<T>(T payload) where T : class =>
        JsonSerializer.SerializeToUtf8Bytes(payload, EventJsonOptions.Web);
}
