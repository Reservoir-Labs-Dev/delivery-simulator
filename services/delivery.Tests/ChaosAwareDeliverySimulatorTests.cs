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

    private ChaosAwareDeliverySimulator NewSut(Func<double>? sample = null) => new(
        _inner, _chaos, NullLogger<ChaosAwareDeliverySimulator>.Instance, sample);

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
    public async Task When_enabled_with_no_params_defaults_to_total_outage()
    {
        // Empty params ⇒ default fail_probability 1.0 ⇒ reproduces DOG-55.
        _chaos.Snapshot = new ChaosConfigSnapshot("delivery_failure_loop", Enabled: true, ParamsJson: "{}");
        var sut = NewSut();

        var act = () => sut.SimulateAsync(NewOrder(), CancellationToken.None);

        await act.Should().ThrowAsync<SimulatedDeliveryException>();
        _inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task When_fail_probability_is_zero_always_passes_through_to_inner()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot(
            "delivery_failure_loop", Enabled: true, ParamsJson: """{"fail_probability":0.0}""");
        // Sampler would return 0 (the lowest roll), yet p=0 must never throw.
        var sut = NewSut(sample: () => 0.0);

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_roll_below_fail_probability_throws_without_calling_inner()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot(
            "delivery_failure_loop", Enabled: true, ParamsJson: """{"fail_probability":0.5}""");
        var sut = NewSut(sample: () => 0.4); // 0.4 < 0.5 ⇒ fail

        var act = () => sut.SimulateAsync(NewOrder(), CancellationToken.None);

        await act.Should().ThrowAsync<SimulatedDeliveryException>();
        _inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task When_roll_at_or_above_fail_probability_passes_through_to_inner()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot(
            "delivery_failure_loop", Enabled: true, ParamsJson: """{"fail_probability":0.5}""");
        var sut = NewSut(sample: () => 0.6); // 0.6 ≥ 0.5 ⇒ succeed this attempt

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_fail_probability_out_of_range_is_clamped()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot(
            "delivery_failure_loop", Enabled: true, ParamsJson: """{"fail_probability":2.5}""");
        // Clamped to 1.0 ⇒ any roll < 1.0 fails.
        var sut = NewSut(sample: () => 0.99);

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
