namespace PaymentService.Chaos;

/// <summary>
/// Canonical chaos scenario names this service consults. Keep in sync with
/// the Dashboard's chaos panel and the M3 plan (DOG-44+).
/// </summary>
public static class ChaosScenarios
{
    /// <summary>DOG-44 — when enabled, sleep <c>params.delay_ms</c> (default 5000) before processing.</summary>
    public const string DelayedPayment = "delayed_payment";
}
