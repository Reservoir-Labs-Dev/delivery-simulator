namespace KitchenService.Simulation;

public sealed class KitchenSimulatorOptions
{
    public const string SectionName = "KitchenSimulator";

    public int MinPrepMs { get; set; } = 200;
    public int MaxPrepMs { get; set; } = 500;
}
