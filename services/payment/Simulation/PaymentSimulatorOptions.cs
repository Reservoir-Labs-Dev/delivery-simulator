namespace PaymentService.Simulation;

public sealed class PaymentSimulatorOptions
{
    public const string SectionName = "PaymentSimulator";

    public int MinDelayMs { get; set; } = 100;
    public int MaxDelayMs { get; set; } = 300;

    /// <summary>
    /// Probability in [0.0, 1.0] that a payment simulation succeeds.
    /// M1 happy path: 1.0 (always succeed). Lowered for chaos experiments.
    /// </summary>
    public double SuccessProbability { get; set; } = 1.0;
}
