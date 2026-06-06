namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// Reads a single chaos scenario row by name. Implementations are expected to
/// be safe to call on the hot path of every message — return <c>null</c> when
/// the scenario row is absent (treat as "disabled by default").
/// </summary>
public interface IChaosConfigReader
{
    Task<ChaosConfigSnapshot?> GetAsync(string name, CancellationToken ct);
}
