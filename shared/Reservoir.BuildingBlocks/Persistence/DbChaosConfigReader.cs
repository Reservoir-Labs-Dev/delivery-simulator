using Microsoft.EntityFrameworkCore;

namespace Reservoir.BuildingBlocks.Persistence;

/// <summary>
/// <see cref="IChaosConfigReader"/> backed by Postgres via EF Core. Uses
/// <see cref="IDbContextFactory{TContext}"/> so it can be safely registered as
/// a singleton — a fresh DbContext is rented per call and disposed at the end
/// of the read.
/// </summary>
public sealed class DbChaosConfigReader : IChaosConfigReader
{
    private readonly IDbContextFactory<ChaosConfigDbContext> _factory;

    public DbChaosConfigReader(IDbContextFactory<ChaosConfigDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<ChaosConfigSnapshot?> GetAsync(string name, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ChaosConfigs
            .AsNoTracking()
            .Where(c => c.Name == name)
            .Select(c => new ChaosConfigSnapshot(c.Name, c.Enabled, c.Params))
            .FirstOrDefaultAsync(ct);
        return row;
    }
}
