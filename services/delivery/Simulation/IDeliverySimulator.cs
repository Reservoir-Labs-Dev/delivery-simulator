using Reservoir.BuildingBlocks.Contracts;

namespace DeliveryService.Simulation;

public interface IDeliverySimulator
{
    Task<DeliveryOutcome> SimulateAsync(OrderReadyEvent order, CancellationToken ct);
}
