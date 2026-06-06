namespace KitchenService.Chaos;

/// <summary>
/// Canonical chaos scenario names this service consults. Keep in sync with
/// the Dashboard's chaos panel and the M3 plan (DOG-47+).
/// </summary>
public static class ChaosScenarios
{
    /// <summary>DOG-47 — when enabled, the kitchen's prep time is multiplied by
    /// <c>params.factor</c> (default 10x). Used to measure SLA-breach count
    /// against a healthy baseline.</summary>
    public const string KitchenSlowdown = "kitchen_slowdown";
}
