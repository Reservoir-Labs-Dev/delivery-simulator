namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// One row per message-handler invocation across payment, kitchen, and
/// delivery (DOG-51). Used by M4 experiments to derive per-stage timing,
/// retry distribution, and DLQ rates. Written exactly once per attempt at
/// the end of the handler (success, skipped-duplicate, or failed paths).
/// </summary>
public class Metric
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The pipeline order that this attempt is for.</summary>
    public Guid OrderId { get; set; }

    /// <summary>Service that produced this row: <c>payment</c>, <c>kitchen</c>, or <c>delivery</c>.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>UTC timestamp captured when the handler entered its work.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>UTC timestamp captured at handler exit (success or thrown).</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>0-based retry count for this delivery (0 = initial, 1..3 = retries).</summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// What happened during this attempt — one of <see cref="MetricOutcomes"/>.
    /// Note: <c>DLQ</c> outcome is not produced by handlers directly; it is
    /// derived in M4 analysis from <c>FAILED</c> rows with
    /// <c>retry_count = 3</c>.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>Canonical values for <see cref="Metric.Outcome"/>.</summary>
public static class MetricOutcomes
{
    public const string Success = "SUCCESS";
    public const string SkippedDuplicate = "SKIPPED_DUPLICATE";
    public const string Failed = "FAILED";
}
