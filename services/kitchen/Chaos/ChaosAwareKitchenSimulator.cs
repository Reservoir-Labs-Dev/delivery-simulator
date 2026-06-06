using System.Text.Json;
using KitchenService.Simulation;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace KitchenService.Chaos;

/// <summary>
/// Decorator over <see cref="IKitchenSimulator"/> that implements the DOG-47
/// "kitchen_slowdown" chaos scenario. On every call, reads the
/// <c>kitchen_slowdown</c> row from <c>chaos.chaos_config</c>; when
/// <c>enabled = true</c>, multiplies the inner simulator's prep duration by
/// <c>params.factor</c> (default 10x) — modelling a backed-up kitchen so the
/// rest of the pipeline shows the SLA-breach behaviour the thesis measures.
/// </summary>
/// <remarks>
/// Strategy: delegate to the inner simulator first to get its baseline
/// <see cref="KitchenPrepOutcome.DurationMs"/>, then sleep an additional
/// <c>(factor - 1) * baselineMs</c> so total wall-clock time ≈ <c>factor *
/// baselineMs</c>. The returned outcome reports the multiplied duration so
/// the downstream <c>order.ready</c> event reflects the slowdown.
/// </remarks>
public sealed class ChaosAwareKitchenSimulator : IKitchenSimulator
{
    /// <summary>Fallback multiplier when the chaos row enables the scenario but
    /// omits <c>factor</c>. Matches the DOG-47 plan text.</summary>
    public const int DefaultFactor = 10;

    private readonly IKitchenSimulator _inner;
    private readonly IChaosConfigReader _chaos;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChaosAwareKitchenSimulator> _logger;

    public ChaosAwareKitchenSimulator(
        IKitchenSimulator inner,
        IChaosConfigReader chaos,
        TimeProvider clock,
        ILogger<ChaosAwareKitchenSimulator> logger)
    {
        _inner = inner;
        _chaos = chaos;
        _clock = clock;
        _logger = logger;
    }

    public async Task<KitchenPrepOutcome> SimulateAsync(PaymentSucceededEvent payment, CancellationToken ct)
    {
        var snapshot = await _chaos.GetAsync(ChaosScenarios.KitchenSlowdown, ct);
        var baseline = await _inner.SimulateAsync(payment, ct);

        if (snapshot is not { Enabled: true })
        {
            return baseline;
        }

        var factor = ParseFactor(snapshot.ParamsJson);
        if (factor <= 1)
        {
            return baseline;
        }

        var extraMs = (long)(factor - 1) * baseline.DurationMs;
        var totalMs = (long)factor * baseline.DurationMs;

        _logger.LogWarning(
            "Chaos [{Scenario}] active: slowing prep for order {OrderId} by factor {Factor} ({BaselineMs}ms -> {TotalMs}ms).",
            ChaosScenarios.KitchenSlowdown, payment.OrderId, factor, baseline.DurationMs, totalMs);

        if (extraMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(extraMs), _clock, ct);
        }

        return new KitchenPrepOutcome((int)Math.Min(totalMs, int.MaxValue));
    }

    private static int ParseFactor(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            return DefaultFactor;

        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return DefaultFactor;

            if (!doc.RootElement.TryGetProperty("factor", out var prop))
                return DefaultFactor;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) && v >= 1)
                return v;

            return DefaultFactor;
        }
        catch (JsonException)
        {
            return DefaultFactor;
        }
    }
}
