namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// Immutable point-in-time view of a <see cref="ChaosConfig"/> row, returned
/// by <see cref="IChaosConfigReader"/>. Value-typed so consumers don't take
/// an EF-Core dependency.
/// </summary>
/// <param name="Name">Scenario identifier (PK).</param>
/// <param name="Enabled">Whether the scenario is currently active.</param>
/// <param name="ParamsJson">Raw <c>jsonb</c> payload as stored in Postgres.</param>
public sealed record ChaosConfigSnapshot(string Name, bool Enabled, string ParamsJson);
