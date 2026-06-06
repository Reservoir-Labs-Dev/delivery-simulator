using FluentAssertions;
using KitchenService.Chaos;
using KitchenService.Simulation;
using KitchenService.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace KitchenService.Tests;

public class ChaosAwareKitchenSimulatorTests
{
    private readonly FakeKitchenSimulator _inner = new() { DurationMsToReport = 300 };
    private readonly StubChaosConfigReader _chaos = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));

    private ChaosAwareKitchenSimulator NewSut() => new(
        _inner, _chaos, _clock, NullLogger<ChaosAwareKitchenSimulator>.Instance);

    private static PaymentSucceededEvent NewPayment() => new(
        EventId: Guid.NewGuid(),
        EventType: RoutingKeys.PaymentSucceeded,
        OccurredAt: DateTimeOffset.UtcNow,
        OrderId: Guid.NewGuid(),
        PaymentId: Guid.NewGuid().ToString(),
        AmountChargedCents: 1000,
        Currency: "USD",
        AttemptNumber: 1);

    [Fact]
    public async Task When_no_chaos_row_passes_through_inner_outcome_unchanged()
    {
        _chaos.Snapshot = null;
        var sut = NewSut();

        var outcome = await sut.SimulateAsync(NewPayment(), CancellationToken.None);

        outcome.DurationMs.Should().Be(300);
        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_chaos_row_disabled_returns_baseline_without_extra_delay()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("kitchen_slowdown", Enabled: false, ParamsJson: """{"factor":10}""");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewPayment(), CancellationToken.None);
        var outcome = await task;

        outcome.DurationMs.Should().Be(300);
        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_enabled_multiplies_duration_by_factor_and_sleeps_remaining_time()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("kitchen_slowdown", Enabled: true, ParamsJson: """{"factor":10}""");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewPayment(), CancellationToken.None);

        // Inner returns immediately (FakeKitchenSimulator doesn't sleep), so the
        // task is now waiting on the extra (factor - 1) * baseline = 9 * 300 = 2700ms.
        task.IsCompleted.Should().BeFalse();

        _clock.Advance(TimeSpan.FromMilliseconds(2699));
        await Task.Yield();
        task.IsCompleted.Should().BeFalse();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var outcome = await task;

        outcome.DurationMs.Should().Be(3000); // 300 * 10
        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_enabled_with_missing_factor_uses_default_of_10()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("kitchen_slowdown", Enabled: true, ParamsJson: "{}");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewPayment(), CancellationToken.None);

        var extraMs = (ChaosAwareKitchenSimulator.DefaultFactor - 1) * 300;
        _clock.Advance(TimeSpan.FromMilliseconds(extraMs - 1));
        await Task.Yield();
        task.IsCompleted.Should().BeFalse();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var outcome = await task;

        outcome.DurationMs.Should().Be(ChaosAwareKitchenSimulator.DefaultFactor * 300);
    }

    [Fact]
    public async Task Garbage_params_json_falls_back_to_default_factor()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("kitchen_slowdown", Enabled: true, ParamsJson: "not json at all");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewPayment(), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMilliseconds((ChaosAwareKitchenSimulator.DefaultFactor - 1) * 300));
        var outcome = await task;

        outcome.DurationMs.Should().Be(ChaosAwareKitchenSimulator.DefaultFactor * 300);
    }

    [Fact]
    public async Task Factor_of_1_returns_baseline_without_extra_delay()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("kitchen_slowdown", Enabled: true, ParamsJson: """{"factor":1}""");
        var sut = NewSut();

        var outcome = await sut.SimulateAsync(NewPayment(), CancellationToken.None);

        outcome.DurationMs.Should().Be(300);
    }

    [Fact]
    public async Task Reader_is_consulted_once_per_call_with_correct_scenario_name()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("kitchen_slowdown", Enabled: false, ParamsJson: "{}");
        var sut = NewSut();

        await sut.SimulateAsync(NewPayment(), CancellationToken.None);
        await sut.SimulateAsync(NewPayment(), CancellationToken.None);

        _chaos.CallCount.Should().Be(2);
        _chaos.LastName.Should().Be("kitchen_slowdown");
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
