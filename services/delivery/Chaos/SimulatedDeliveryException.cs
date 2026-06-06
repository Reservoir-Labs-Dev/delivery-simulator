namespace DeliveryService.Chaos;

/// <summary>
/// Thrown by <see cref="ChaosAwareDeliverySimulator"/> when the
/// <c>delivery_failure_loop</c> chaos scenario is active. Propagates out of
/// the handler so the consumer's retry pipeline (1s/2s/4s → DLQ) exercises
/// the failure path under sustained injected failure.
/// </summary>
public sealed class SimulatedDeliveryException : Exception
{
    public SimulatedDeliveryException()
        : base("Simulated delivery failure (delivery_failure_loop chaos scenario enabled).")
    {
    }
}
