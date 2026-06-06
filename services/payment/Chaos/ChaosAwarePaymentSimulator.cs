using System.Text.Json;
using PaymentService.Simulation;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace PaymentService.Chaos;

/// <summary>
/// Decorator over <see cref="IPaymentSimulator"/> that implements the DOG-44
/// "delayed_payment" chaos scenario. On every call, reads the
/// <c>delayed_payment</c> row from <c>chaos.chaos_config</c>; when
/// <c>enabled = true</c>, sleeps <c>params.delay_ms</c> (default 5000ms)
/// before delegating to the wrapped simulator — modelling a slow external
/// payment gateway.
/// </summary>
public sealed class ChaosAwarePaymentSimulator : IPaymentSimulator
{
    /// <summary>Fallback delay when the chaos row enables the scenario but
    /// omits <c>delay_ms</c>. Matches the plan text.</summary>
    public const int DefaultDelayMs = 5000;

    private readonly IPaymentSimulator _inner;
    private readonly IChaosConfigReader _chaos;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChaosAwarePaymentSimulator> _logger;

    public ChaosAwarePaymentSimulator(
        IPaymentSimulator inner,
        IChaosConfigReader chaos,
        TimeProvider clock,
        ILogger<ChaosAwarePaymentSimulator> logger)
    {
        _inner = inner;
        _chaos = chaos;
        _clock = clock;
        _logger = logger;
    }

    public async Task<PaymentOutcome> SimulateAsync(OrderCreatedEvent order, CancellationToken ct)
    {
        var snapshot = await _chaos.GetAsync(ChaosScenarios.DelayedPayment, ct);
        if (snapshot is { Enabled: true })
        {
            var delayMs = ParseDelayMs(snapshot.ParamsJson);
            _logger.LogWarning(
                "Chaos [{Scenario}] active: delaying order {OrderId} by {DelayMs}ms before processing.",
                ChaosScenarios.DelayedPayment, order.OrderId, delayMs);
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _clock, ct);
        }

        return await _inner.SimulateAsync(order, ct);
    }

    private static int ParseDelayMs(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            return DefaultDelayMs;

        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return DefaultDelayMs;

            if (!doc.RootElement.TryGetProperty("delay_ms", out var prop))
                return DefaultDelayMs;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) && v >= 0)
                return v;

            return DefaultDelayMs;
        }
        catch (JsonException)
        {
            return DefaultDelayMs;
        }
    }
}
