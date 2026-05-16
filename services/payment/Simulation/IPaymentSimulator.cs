using Reservoir.BuildingBlocks.Contracts;

namespace PaymentService.Simulation;

public interface IPaymentSimulator
{
    Task<PaymentOutcome> SimulateAsync(OrderCreatedEvent order, CancellationToken ct);
}
