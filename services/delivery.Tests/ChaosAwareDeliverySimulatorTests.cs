using DeliveryService.Chaos;
using DeliveryService.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace DeliveryService.Tests;

public class ChaosAwareDeliverySimulatorTests
{
    private readonly FakeDeliverySimulator _inner = new() { ShouldSucceed = true };
    private readonly StubChaosConfigReader _chaos = new();

    private ChaosAwareDeliverySimulator NewSut() => new(
        _inner, _chaos, NullLogger<ChaosAwareDeliverySimulator>.Instance);

    private static OrderReadyEvent NewOrder() => new(
        EventId: Guid.NewGuid(),
        EventType: RoutingKeys.OrderReady,
        OccurredAt: DateTimeOffset.UtcNow,
        OrderId: Guid.NewGuid(),
        PreparedAt: DateTimeOffset.UtcNow,
        PrepDurationMs: 100,
        Items: new[] { new ReadyItem("item-1", "X", 1) });

    [Fact]
    public async Task When_no_chaos_row_exists_passes_through_to_inner()
    {
        _chaos.Snapshot = null;
        var sut = NewSut();

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_chaos_row_disabled_passes_through_to_inner()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delivery_failure_loop", Enabled: false, ParamsJson: "{}");
        var sut = NewSut();

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_enabled_throws_SimulatedDeliveryException_without_calling_inner()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delivery_failure_loop", Enabled: true, ParamsJson: "{}");
        var sut = NewSut();

        var act = () => sut.SimulateAsync(NewOrder(), CancellationToken.None);

        await act.Should().ThrowAsync<SimulatedDeliveryException>();
        _inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Reader_is_consulted_once_per_call_with_correct_scenario_name()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delivery_failure_loop", Enabled: false, ParamsJson: "{}");
        var sut = NewSut();

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);
        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _chaos.CallCount.Should().Be(2);
        _chaos.LastName.Should().Be("delivery_failure_loop");
    }

    private sealed class StubChaosConfigReader : IChaosConfigReader
    {
        public ChaosConfigSnapshot? Snapshot { get; set; }
        public int CallCount { get; private set; }
        public string? LastName { get; private set; }

        public Task<ChaosConfigSnapshot?> GetAsync(string name, CancellationToken ct)
        {
            CallCount++;
            LastName = name;
            return Task.FromResult(Snapshot);
        }
    }
}
