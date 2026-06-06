using System.Text.Json;

namespace DashboardApi.Api;

/// <summary>
/// Request body for <c>POST /chaos/set</c>. <see cref="Params"/> is an opaque
/// JSON object the handler stores verbatim into the <c>jsonb</c> column.
/// </summary>
public sealed record ChaosConfigSetRequest(string Name, bool Enabled, JsonElement? Params);
