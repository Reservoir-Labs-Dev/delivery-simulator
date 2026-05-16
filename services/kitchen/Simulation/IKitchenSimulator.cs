using Reservoir.BuildingBlocks.Contracts;

namespace KitchenService.Simulation;

public interface IKitchenSimulator
{
    Task<KitchenPrepOutcome> SimulateAsync(PaymentSucceededEvent payment, CancellationToken ct);
}
