using System.Text.Json;
using DashboardApi.Api;
using DashboardApi.Data;
using Microsoft.EntityFrameworkCore;
using Reservoir.BuildingBlocks.Persistence;

namespace DashboardApi.Handlers;

/// <summary>
/// Idempotent upsert for <see cref="ChaosConfig"/>. Existing row is updated in
/// place; missing row is inserted. <c>name</c> is the primary key, so each
/// scenario keeps a single canonical row.
/// </summary>
public sealed class ChaosConfigUpsertHandler
{
    private readonly ChaosConfigDbContext _db;
    private readonly TimeProvider _clock;

    public ChaosConfigUpsertHandler(ChaosConfigDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ChaosConfig> HandleAsync(ChaosConfigSetRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Chaos scenario name must not be empty.", nameof(request));

        var paramsJson = SerializeParams(request.Params);

        var row = await _db.ChaosConfigs.FirstOrDefaultAsync(c => c.Name == request.Name, ct);
        if (row is null)
        {
            row = new ChaosConfig { Name = request.Name };
            _db.ChaosConfigs.Add(row);
        }

        row.Enabled = request.Enabled;
        row.Params = paramsJson;
        row.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static string SerializeParams(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null || value.Value.ValueKind == JsonValueKind.Undefined)
            return "{}";

        if (value.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chaos params must be a JSON object.", nameof(value));

        return value.Value.GetRawText();
    }
}
