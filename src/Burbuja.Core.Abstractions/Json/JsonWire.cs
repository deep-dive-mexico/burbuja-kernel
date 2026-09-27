using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Burbuja.Core.Abstractions.Json;

/// <summary>Reflection-based wire options (JsonNode is not source-gen friendly).</summary>
public static class JsonWire
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.Converters.Add(new JsonStringEnumConverter(options.PropertyNamingPolicy, allowIntegerValues: false));
        return options;
    }
}
