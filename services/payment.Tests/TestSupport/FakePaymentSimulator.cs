using PaymentService.Domain;
using PaymentService.Simulation;
using Reservoir.BuildingBlocks.Contracts;

namespace PaymentService.Tests.TestSupport;

internal sealed class FakePaymentSimulator : IPaymentSimulator
{
    public bool ShouldSucceed { get; set; } = true;
    public string FailureReason { get; set; } = PaymentFailureReason.PaymentDeclined;
    public int DelayMsToReport { get; set; } = 0;
    public int CallCount { get; set; }

    public Task<PaymentOutcome> SimulateAsync(OrderCreatedEvent order, CancellationToken ct)
    {
        CallCount++;
        var outcome = ShouldSucceed
            ? PaymentOutcome.Succeeded(DelayMsToReport)
            : PaymentOutcome.Failed(FailureReason, DelayMsToReport);
        return Task.FromResult(outcome);
    }
}
