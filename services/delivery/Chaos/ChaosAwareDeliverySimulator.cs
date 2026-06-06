using DeliveryService.Simulation;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Persistence;

namespace DeliveryService.Chaos;

/// <summary>
/// Decorator over <see cref="IDeliverySimulator"/> that implements the DOG-45
/// "delivery_failure_loop" chaos scenario. On every call, reads the
/// <c>delivery_failure_loop</c> row from <c>chaos.chaos_config</c>; when
/// <c>enabled = true</c>, throws <see cref="SimulatedDeliveryException"/>
/// before any work happens — forcing the consumer's retry pipeline through
/// 3 retries and into the delivery DLQ.
/// </summary>
public sealed class ChaosAwareDeliverySimulator : IDeliverySimulator
{
    private readonly IDeliverySimulator _inner;
    private readonly IChaosConfigReader _chaos;
    private readonly ILogger<ChaosAwareDeliverySimulator> _logger;

    public ChaosAwareDeliverySimulator(
        IDeliverySimulator inner,
        IChaosConfigReader chaos,
        ILogger<ChaosAwareDeliverySimulator> logger)
    {
        _inner = inner;
        _chaos = chaos;
        _logger = logger;
    }

    public async Task<DeliveryOutcome> SimulateAsync(OrderReadyEvent order, CancellationToken ct)
    {
        var snapshot = await _chaos.GetAsync(ChaosScenarios.DeliveryFailureLoop, ct);
        if (snapshot is { Enabled: true })
        {
            _logger.LogWarning(
                "Chaos [{Scenario}] active: throwing SimulatedDeliveryException for order {OrderId}.",
                ChaosScenarios.DeliveryFailureLoop, order.OrderId);
            throw new SimulatedDeliveryException();
        }

        return await _inner.SimulateAsync(order, ct);
    }
}
