using System.Text.Json;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Cluster;

/// <summary>The kernel's wire conventions (camelCase, string enums, nulls omitted) for everything the cluster reads or writes.</summary>
public static class ClusterJson
{
    public static JsonSerializerOptions Options => JsonWire.Options;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static byte[] WriteBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Read<T>(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }

    public static T? Read<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }
}
