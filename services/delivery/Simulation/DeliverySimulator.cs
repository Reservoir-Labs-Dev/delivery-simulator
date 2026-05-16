using DeliveryService.Domain;
using Microsoft.Extensions.Options;
using Reservoir.BuildingBlocks.Contracts;

namespace DeliveryService.Simulation;

public sealed class DeliverySimulator : IDeliverySimulator
{
    private readonly DeliverySimulatorOptions _options;
    private readonly ILogger<DeliverySimulator> _logger;

    public DeliverySimulator(IOptions<DeliverySimulatorOptions> options, ILogger<DeliverySimulator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.MinDelayMs < 0 || _options.MaxDelayMs < _options.MinDelayMs)
            throw new InvalidOperationException(
                $"Invalid DeliverySimulator delay range: [{_options.MinDelayMs}, {_options.MaxDelayMs}]");
        if (_options.SuccessProbability < 0 || _options.SuccessProbability > 1)
            throw new InvalidOperationException(
                $"DeliverySimulator.SuccessProbability must be in [0, 1], got {_options.SuccessProbability}");
    }

    public async Task<DeliveryOutcome> SimulateAsync(OrderReadyEvent order, CancellationToken ct)
    {
        var delayMs = Random.Shared.Next(_options.MinDelayMs, _options.MaxDelayMs + 1);
        await Task.Delay(delayMs, ct);

        var success = Random.Shared.NextDouble() < _options.SuccessProbability;
        _logger.LogDebug(
            "Simulated delivery for order {OrderId}: success={Success}, delay={DelayMs}ms",
            order.OrderId, success, delayMs);

        return success
            ? DeliveryOutcome.Succeeded(delayMs)
            : DeliveryOutcome.Failed(DeliveryFailureReason.DriverUnavailable, delayMs);
    }
}
