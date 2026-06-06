namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// Chaos engineering toggle row. One per chaos scenario name (e.g.
/// <c>delayed_payment</c>, <c>delivery_failure_loop</c>). The Dashboard writes
/// rows via <c>POST /chaos/set</c>; each consumer service reads its scenario
/// row on every message in M3.
/// </summary>
public class ChaosConfig
{
    /// <summary>Scenario identifier; primary key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the scenario is currently active.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-scenario knobs (e.g. <c>{ "delay_ms": 5000 }</c>). Stored as
    /// Postgres <c>jsonb</c>; opaque to this row — each consumer parses its own
    /// shape.
    /// </summary>
    public string Params { get; set; } = "{}";

    /// <summary>Last write timestamp; set by the upsert handler.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
