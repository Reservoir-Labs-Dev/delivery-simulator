namespace OrderService.Chaos;

/// <summary>
/// Canonical chaos scenario names this service consults. Keep in sync with
/// the Dashboard's chaos panel and the M3 plan (DOG-46+).
/// </summary>
public static class ChaosScenarios
{
    /// <summary>DOG-46 — when enabled, the order-service publishes
    /// <c>order.created</c> N times (default 3) with the SAME message_id,
    /// exercising downstream services' idempotent-consumer pattern.</summary>
    public const string DuplicateEvents = "duplicate_events";
}
