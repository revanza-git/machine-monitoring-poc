using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared;

/// <summary>
/// One serializer configuration for agent and server so the JSON on the wire
/// is exactly the snake_case shape in the design document.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
