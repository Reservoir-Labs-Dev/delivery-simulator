namespace DeliveryService.Chaos;

/// <summary>
/// Canonical chaos scenario names this service consults. Keep in sync with
/// the Dashboard's chaos panel and the M3 plan (DOG-45+).
/// </summary>
public static class ChaosScenarios
{
    /// <summary>DOG-45 / DOG-133 — when enabled, each delivery attempt throws
    /// <see cref="SimulatedDeliveryException"/> with probability
    /// <c>params.fail_probability</c> (default 1.0). At 1.0 every order is forced
    /// through 3 retries → DLQ (total outage); below 1.0 retries can rescue an
    /// order that failed an earlier attempt.</summary>
    public const string DeliveryFailureLoop = "delivery_failure_loop";
}
