namespace DeliveryService.Simulation;

public sealed class DeliverySimulatorOptions
{
    public const string SectionName = "DeliverySimulator";

    public int MinDelayMs { get; set; } = 300;
    public int MaxDelayMs { get; set; } = 700;

    /// <summary>
    /// Probability in [0.0, 1.0] that a delivery simulation succeeds.
    /// M1 happy path: 1.0 (always succeed). Chaos scenario 2 lowers this to
    /// exercise the delivery failure loop.
    /// </summary>
    public double SuccessProbability { get; set; } = 1.0;
}
