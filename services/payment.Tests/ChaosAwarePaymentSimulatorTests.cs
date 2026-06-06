using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PaymentService.Chaos;
using PaymentService.Tests.TestSupport;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace PaymentService.Tests;

public class ChaosAwarePaymentSimulatorTests
{
    private readonly FakePaymentSimulator _inner = new() { ShouldSucceed = true };
    private readonly StubChaosConfigReader _chaos = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));

    private ChaosAwarePaymentSimulator NewSut() => new(
        _inner, _chaos, _clock, NullLogger<ChaosAwarePaymentSimulator>.Instance);

    private static OrderCreatedEvent NewOrder() => new(
        EventId: Guid.NewGuid(),
        EventType: RoutingKeys.OrderCreated,
        OccurredAt: DateTimeOffset.UtcNow,
        OrderId: Guid.NewGuid(),
        CustomerId: "cust-1",
        Items: new[] { new OrderEventItem("item-1", "X", 1, 100) },
        TotalAmountCents: 100,
        Currency: "USD");

    [Fact]
    public async Task When_no_chaos_row_exists_passes_through_to_inner()
    {
        _chaos.Snapshot = null;
        var sut = NewSut();

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_chaos_row_disabled_does_not_delay()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delayed_payment", Enabled: false, ParamsJson: """{"delay_ms":5000}""");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewOrder(), CancellationToken.None);
        await task; // completes synchronously without advancing the clock

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_enabled_delays_by_delay_ms_then_calls_inner()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delayed_payment", Enabled: true, ParamsJson: """{"delay_ms":5000}""");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewOrder(), CancellationToken.None);

        // Inner must NOT have been called before the delay completes.
        task.IsCompleted.Should().BeFalse();
        _inner.CallCount.Should().Be(0);

        _clock.Advance(TimeSpan.FromMilliseconds(4999));
        await Task.Yield();
        _inner.CallCount.Should().Be(0);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await task;
        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task When_enabled_with_missing_delay_uses_5000ms_default()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delayed_payment", Enabled: true, ParamsJson: "{}");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMilliseconds(ChaosAwarePaymentSimulator.DefaultDelayMs - 1));
        await Task.Yield();
        task.IsCompleted.Should().BeFalse();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await task;
        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Garbage_params_json_falls_back_to_default_delay()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delayed_payment", Enabled: true, ParamsJson: "not json at all");
        var sut = NewSut();

        var task = sut.SimulateAsync(NewOrder(), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(ChaosAwarePaymentSimulator.DefaultDelayMs));
        await task;

        _inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Reader_is_consulted_once_per_call()
    {
        _chaos.Snapshot = new ChaosConfigSnapshot("delayed_payment", Enabled: false, ParamsJson: "{}");
        var sut = NewSut();

        await sut.SimulateAsync(NewOrder(), CancellationToken.None);
        await sut.SimulateAsync(NewOrder(), CancellationToken.None);

        _chaos.CallCount.Should().Be(2);
        _chaos.LastName.Should().Be("delayed_payment");
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
