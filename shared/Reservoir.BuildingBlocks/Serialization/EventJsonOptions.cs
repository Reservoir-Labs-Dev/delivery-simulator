using System.Text.Json;

namespace Reservoir.BuildingBlocks.Serialization;

public static class EventJsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
