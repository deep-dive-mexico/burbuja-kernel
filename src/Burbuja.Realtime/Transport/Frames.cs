using System.Buffers;
using System.Text.Json;
using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Transport;

/// <summary>Encodes the outbound envelope <c>{"t":type,"k":tick,"d":payload}</c> once per message.</summary>
public static class Frames
{
    /// <summary>The kernel's own options, for <c>sys.*</c> payloads and the HTTP API.</summary>
    public static JsonSerializerOptions Json => RealtimeJson.Default;

    public static byte[] Encode<T>(string type, long tick, T payload, JsonSerializerOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", type);
            writer.WriteNumber("k", tick);
            writer.WritePropertyName("d");
            JsonSerializer.Serialize(writer, payload, options ?? Json);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the inbound envelope. False for anything that is not an object with a short string
    /// <c>t</c>; the data element is cloned so it outlives the transport buffer.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out string type, out JsonElement data, out long? sequence)
    {
        type = "";
        data = default;
        sequence = null;
        try
        {
            var reader = new Utf8JsonReader(frame, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
                return false;
            type = t.GetString()!;
            if (type.Length is 0 or > 64)
                return false;
            if (root.TryGetProperty("d", out var d))
                data = d.Clone();
            if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var seq))
                sequence = seq;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
