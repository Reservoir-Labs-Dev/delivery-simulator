using System.Text.Json;
using DeliveryService.Simulation;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace DeliveryService.Chaos;

/// <summary>
/// Decorator over <see cref="IDeliverySimulator"/> that implements the
/// "delivery_failure_loop" chaos scenario. On every call, reads the
/// <c>delivery_failure_loop</c> row from <c>chaos.chaos_config</c>; when
/// <c>enabled = true</c>, throws <see cref="SimulatedDeliveryException"/> with
/// probability <c>params.fail_probability</c> (default 1.0) <em>before</em> any
/// work happens.
/// </summary>
/// <remarks>
/// <para>
/// DOG-45 introduced this as an unconditional throw (total outage). DOG-133
/// generalises it to a per-attempt failure probability so the experiment can
/// measure <em>retry rescue</em>, not just containment:
/// </para>
/// <list type="bullet">
/// <item><c>fail_probability = 1.0</c> (default) reproduces the original
/// DOG-55 total outage exactly — every attempt fails, every order ends in the
/// DLQ. Existing experiments are unchanged.</item>
/// <item><c>0 &lt; fail_probability &lt; 1</c> fails each delivery attempt
/// independently. Because every redelivery re-enters this decorator and rolls
/// afresh, an order that fails its first attempt can still succeed on a retry —
/// the rescued-by-retry population the DOG-133 experiment quantifies. With
/// p and up to 4 attempts (initial + 3 retries), expected loss ≈ p⁴.</item>
/// </list>
/// <para>
/// Sweeping <c>fail_probability</c> from 0→1 over successive runs yields the
/// rescue-rate-vs-failure-rate curve.
/// </para>
/// </remarks>
public sealed class ChaosAwareDeliverySimulator : IDeliverySimulator
{
    /// <summary>Fallback failure probability when the scenario is enabled but
    /// omits <c>fail_probability</c>. 1.0 preserves the original total-outage
    /// behaviour so prior experiments stay reproducible.</summary>
    public const double DefaultFailProbability = 1.0;

    private readonly IDeliverySimulator _inner;
    private readonly IChaosConfigReader _chaos;
    private readonly Func<double> _sample;
    private readonly ILogger<ChaosAwareDeliverySimulator> _logger;

    public ChaosAwareDeliverySimulator(
        IDeliverySimulator inner,
        IChaosConfigReader chaos,
        ILogger<ChaosAwareDeliverySimulator> logger,
        Func<double>? sample = null)
    {
        _inner = inner;
        _chaos = chaos;
        _logger = logger;
        // Sampler in [0, 1); seam lets tests force deterministic outcomes.
        _sample = sample ?? Random.Shared.NextDouble;
    }

    public async Task<DeliveryOutcome> SimulateAsync(OrderReadyEvent order, CancellationToken ct)
    {
        var snapshot = await _chaos.GetAsync(ChaosScenarios.DeliveryFailureLoop, ct);
        if (snapshot is { Enabled: true })
        {
            var failProbability = ParseFailProbability(snapshot.ParamsJson);
            if (failProbability > 0 && _sample() < failProbability)
            {
                _logger.LogWarning(
                    "Chaos [{Scenario}] active (p={FailProbability}): throwing SimulatedDeliveryException for order {OrderId}.",
                    ChaosScenarios.DeliveryFailureLoop, failProbability, order.OrderId);
                throw new SimulatedDeliveryException();
            }
        }

        return await _inner.SimulateAsync(order, ct);
    }

    private static double ParseFailProbability(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            return DefaultFailProbability;

        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return DefaultFailProbability;

            if (!doc.RootElement.TryGetProperty("fail_probability", out var prop))
                return DefaultFailProbability;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var v))
                return Math.Clamp(v, 0.0, 1.0);

            return DefaultFailProbability;
        }
        catch (JsonException)
        {
            return DefaultFailProbability;
        }
    }
}
