using KitchenService.Simulation;
using Reservoir.BuildingBlocks.Contracts;

namespace KitchenService.Tests.TestSupport;

internal sealed class FakeKitchenSimulator : IKitchenSimulator
{
    public int DurationMsToReport { get; set; } = 250;
    public int CallCount { get; set; }

    public Task<KitchenPrepOutcome> SimulateAsync(PaymentSucceededEvent payment, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(new KitchenPrepOutcome(DurationMsToReport));
    }
}
