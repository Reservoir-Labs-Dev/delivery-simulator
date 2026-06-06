using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Chaos;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;
using Reservoir.TestSupport;

namespace OrderService.Tests;

public class ChaosAwareEventPublisherTests
{
    private readonly FakeEventPublisher _inner = new();
    private readonly StubChaosConfigReader _chaos = new();

    private ChaosAwareEventPublisher NewSut() => new(
        _inner, _chaos, NullLogger<ChaosAwareEventPublisher>.Instance);

    private static OrderCreatedEvent NewOrder(Guid? orderId = null) => new(
        EventId: Guid.NewGuid(),
        EventType: RoutingKeys.OrderCreated,
        OccurredAt: DateTimeOffset.UtcNow,
        OrderId: orderId ?? Guid.NewGuid(),
        CustomerId: "cust-1",
        Items: new[] { new OrderEventItem("item-1", "X", 1, 100) },
        TotalAmountCents: 100,
        Currency: "USD");

    [Fact]
    public void When_no_chaos_row_publishes_once()
    {
        _chaos.Snapshot = null;
        var sut = NewSut();

        var msgId = Guid.NewGuid();
        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), msgId, DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(1);
        _inner.Published[0].MessageId.Should().Be(msgId);
    }

    [Fact]
    public void When_chaos_row_disabled_publishes_once()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: false, ParamsJson: """{"count":3}""");
        var sut = NewSut();

        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(1);
    }

    [Fact]
    public void When_enabled_publishes_N_copies_with_same_message_id()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: true, ParamsJson: """{"count":3}""");
        var sut = NewSut();

        var msgId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), msgId, occurredAt);

        _inner.Published.Should().HaveCount(3);
        _inner.Published.Select(p => p.MessageId).Should().AllBeEquivalentTo(msgId);
        _inner.Published.Select(p => p.OccurredAt).Should().AllBeEquivalentTo(occurredAt);
        _inner.Published.Select(p => p.RoutingKey).Should().AllBeEquivalentTo(RoutingKeys.OrderCreated);
    }

    [Fact]
    public void When_enabled_with_missing_count_uses_default_of_3()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: true, ParamsJson: "{}");
        var sut = NewSut();

        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(ChaosAwareEventPublisher.DefaultDuplicateCount);
    }

    [Fact]
    public void Garbage_params_json_falls_back_to_default_count()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: true, ParamsJson: "not json at all");
        var sut = NewSut();

        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(ChaosAwareEventPublisher.DefaultDuplicateCount);
    }

    [Fact]
    public void Count_below_1_clamps_to_single_publish()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: true, ParamsJson: """{"count":0}""");
        var sut = NewSut();

        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(ChaosAwareEventPublisher.DefaultDuplicateCount);
    }

    [Fact]
    public void Non_order_created_routing_keys_pass_through_without_consulting_chaos()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: true, ParamsJson: """{"count":5}""");
        var sut = NewSut();

        sut.Publish("some.other.event", new { foo = 1 }, Guid.NewGuid(), DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(1);
        _chaos.CallCount.Should().Be(0);
    }

    [Fact]
    public void Reader_is_consulted_once_per_publish_with_correct_scenario_name()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("duplicate_events", Enabled: false, ParamsJson: "{}");
        var sut = NewSut();

        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        _chaos.CallCount.Should().Be(2);
        _chaos.LastName.Should().Be("duplicate_events");
    }

    [Fact]
    public void Reader_failure_falls_back_to_single_publish()
    {
        _chaos.Throw = new InvalidOperationException("db down");
        var sut = NewSut();

        sut.Publish(RoutingKeys.OrderCreated, NewOrder(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        _inner.Published.Should().HaveCount(1);
    }

    private sealed class StubChaosConfigReader : IChaosConfigReader
    {
        public ChaosConfigSnapshot? Snapshot { get; set; }
        public Exception? Throw { get; set; }
        public int CallCount { get; private set; }
        public string? LastName { get; private set; }

        public Task<ChaosConfigSnapshot?> GetAsync(string name, CancellationToken ct)
        {
            CallCount++;
            LastName = name;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Snapshot);
        }
    }
}
