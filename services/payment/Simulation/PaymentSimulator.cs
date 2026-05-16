using Microsoft.Extensions.Options;
using PaymentService.Domain;
using Reservoir.BuildingBlocks.Contracts;

namespace PaymentService.Simulation;

public sealed class PaymentSimulator : IPaymentSimulator
{
    private readonly PaymentSimulatorOptions _options;
    private readonly ILogger<PaymentSimulator> _logger;

    public PaymentSimulator(IOptions<PaymentSimulatorOptions> options, ILogger<PaymentSimulator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.MinDelayMs < 0 || _options.MaxDelayMs < _options.MinDelayMs)
            throw new InvalidOperationException(
                $"Invalid PaymentSimulator delay range: [{_options.MinDelayMs}, {_options.MaxDelayMs}]");
        if (_options.SuccessProbability < 0 || _options.SuccessProbability > 1)
            throw new InvalidOperationException(
                $"PaymentSimulator.SuccessProbability must be in [0, 1], got {_options.SuccessProbability}");
    }

    public async Task<PaymentOutcome> SimulateAsync(OrderCreatedEvent order, CancellationToken ct)
    {
        var delayMs = Random.Shared.Next(_options.MinDelayMs, _options.MaxDelayMs + 1);
        await Task.Delay(delayMs, ct);

        var success = Random.Shared.NextDouble() < _options.SuccessProbability;
        _logger.LogDebug(
            "Simulated payment for order {OrderId}: success={Success}, delay={DelayMs}ms",
            order.OrderId, success, delayMs);

        return success
            ? PaymentOutcome.Succeeded(delayMs)
            : PaymentOutcome.Failed(PaymentFailureReason.PaymentDeclined, delayMs);
    }
}
