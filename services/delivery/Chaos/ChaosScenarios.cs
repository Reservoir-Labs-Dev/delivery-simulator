namespace DeliveryService.Chaos;

/// <summary>
/// Canonical chaos scenario names this service consults. Keep in sync with
/// the Dashboard's chaos panel and the M3 plan (DOG-45+).
/// </summary>
public static class ChaosScenarios
{
    /// <summary>DOG-45 — when enabled, every delivery throws <see cref="SimulatedDeliveryException"/>,
    /// forcing the consumer through 3 retries → DLQ.</summary>
    public const string DeliveryFailureLoop = "delivery_failure_loop";
}
