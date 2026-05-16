using DeliveryService.Domain;
using DeliveryService.Simulation;
using Reservoir.BuildingBlocks.Contracts;

namespace DeliveryService.Tests.TestSupport;

internal sealed class FakeDeliverySimulator : IDeliverySimulator
{
    public bool ShouldSucceed { get; set; } = true;
    public string FailureReason { get; set; } = DeliveryFailureReason.DriverUnavailable;
    public int DelayMsToReport { get; set; } = 0;
    public int CallCount { get; set; }

    public Task<DeliveryOutcome> SimulateAsync(OrderReadyEvent order, CancellationToken ct)
    {
        CallCount++;
        var outcome = ShouldSucceed
            ? DeliveryOutcome.Succeeded(DelayMsToReport)
            : DeliveryOutcome.Failed(FailureReason, DelayMsToReport);
        return Task.FromResult(outcome);
    }
}
