using Microsoft.Extensions.Options;
using Reservoir.BuildingBlocks.Contracts;

namespace KitchenService.Simulation;

public sealed class KitchenSimulator : IKitchenSimulator
{
    private readonly KitchenSimulatorOptions _options;
    private readonly ILogger<KitchenSimulator> _logger;

    public KitchenSimulator(IOptions<KitchenSimulatorOptions> options, ILogger<KitchenSimulator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.MinPrepMs < 0 || _options.MaxPrepMs < _options.MinPrepMs)
            throw new InvalidOperationException(
                $"Invalid KitchenSimulator prep range: [{_options.MinPrepMs}, {_options.MaxPrepMs}]");
    }

    public async Task<KitchenPrepOutcome> SimulateAsync(PaymentSucceededEvent payment, CancellationToken ct)
    {
        var durationMs = Random.Shared.Next(_options.MinPrepMs, _options.MaxPrepMs + 1);
        await Task.Delay(durationMs, ct);

        _logger.LogDebug(
            "Simulated kitchen prep for order {OrderId}: duration={DurationMs}ms",
            payment.OrderId, durationMs);

        return new KitchenPrepOutcome(durationMs);
    }
}
