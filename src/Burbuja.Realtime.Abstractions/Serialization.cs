using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Burbuja.Realtime.Abstractions;

/// <summary>
/// The JSON settings game payloads are written and read with: web defaults (camelCase, case-insensitive reads)
/// plus <see cref="Vector2"/>, <see cref="Vector3"/>, <see cref="Vector4"/> and <see cref="Quaternion"/> as
/// <c>{"x":…,"y":…}</c> objects. Without those converters System.Text.Json writes the numerics types as
/// <c>{}</c>, because their components are fields.
/// </summary>
public static class RealtimeJson
{
    /// <summary>The options a game gets when <see cref="IRealtimeGame.SerializerOptions"/> is null. Read-only.</summary>
    public static JsonSerializerOptions Default { get; } = Freeze(CreateOptions());

    /// <summary>A fresh, mutable copy of <see cref="Default"/> for a game to extend with its own converters or a
    /// source-generated <see cref="JsonSerializerOptions.TypeInfoResolver"/>.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new Vector2Converter());
        options.Converters.Add(new Vector3Converter());
        options.Converters.Add(new Vector4Converter());
        options.Converters.Add(new QuaternionConverter());
        return options;
    }

    private static JsonSerializerOptions Freeze(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private abstract class ComponentConverter<T> : JsonConverter<T> where T : struct
    {
        private readonly string[] _names;
        private readonly string[] _upper;

        protected ComponentConverter(params string[] names)
        {
            _names = names;
            _upper = names.Select(n => n.ToUpperInvariant()).ToArray();
        }

        protected abstract T Create(ReadOnlySpan<float> components);

        protected abstract void Components(T value, Span<float> components);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"A {typeof(T).Name} is an object of {string.Join(", ", _names)}.");
            Span<float> components = stackalloc float[_names.Length];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException();
                var index = -1;
                for (var i = 0; i < _names.Length && index < 0; i++)
                    if (reader.ValueTextEquals(_names[i]) || reader.ValueTextEquals(_upper[i]))
                        index = i;
                reader.Read();
                if (index < 0)
                    reader.Skip();
                else
                    components[index] = reader.GetSingle();
            }

            return Create(components);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            Span<float> components = stackalloc float[_names.Length];
            Components(value, components);
            writer.WriteStartObject();
            for (var i = 0; i < _names.Length; i++)
                writer.WriteNumber(_names[i], components[i]);
            writer.WriteEndObject();
        }
    }

    private sealed class Vector2Converter() : ComponentConverter<Vector2>("x", "y")
    {
        protected override Vector2 Create(ReadOnlySpan<float> c) => new(c[0], c[1]);

        protected override void Components(Vector2 v, Span<float> c) => (c[0], c[1]) = (v.X, v.Y);
    }

    private sealed class Vector3Converter() : ComponentConverter<Vector3>("x", "y", "z")
    {
        protected override Vector3 Create(ReadOnlySpan<float> c) => new(c[0], c[1], c[2]);

        protected override void Components(Vector3 v, Span<float> c) => (c[0], c[1], c[2]) = (v.X, v.Y, v.Z);
    }

    private sealed class Vector4Converter() : ComponentConverter<Vector4>("x", "y", "z", "w")
    {
        protected override Vector4 Create(ReadOnlySpan<float> c) => new(c[0], c[1], c[2], c[3]);

        protected override void Components(Vector4 v, Span<float> c) => (c[0], c[1], c[2], c[3]) = (v.X, v.Y, v.Z, v.W);
    }

    private sealed class QuaternionConverter() : ComponentConverter<Quaternion>("x", "y", "z", "w")
    {
        protected override Quaternion Create(ReadOnlySpan<float> c) => new(c[0], c[1], c[2], c[3]);

        protected override void Components(Quaternion q, Span<float> c) => (c[0], c[1], c[2], c[3]) = (q.X, q.Y, q.Z, q.W);
    }
}

/// <summary>
/// The binary envelope, for games that pack their own payloads (hand-written structs, MessagePack,
/// FlatBuffers). One WebSocket binary message is <c>[u8 n][n bytes: type, ASCII][i64 little-endian][payload]</c>.
/// The number is the server tick outbound and the client's sequence inbound, the same roles as <c>k</c> and
/// <c>s</c> in a JSON frame. Kernel (<c>sys.*</c>) messages are always JSON text.
/// </summary>
public static class RealtimeBinary
{
    public const int MaxTypeLength = 64;

    /// <summary>The header size for a type of <paramref name="typeLength"/> ASCII characters.</summary>
    public static int HeaderLength(int typeLength) => 1 + typeLength + sizeof(long);

    public static byte[] Encode(string type, long number, ReadOnlySpan<byte> payload)
    {
        if (string.IsNullOrEmpty(type) || type.Length > MaxTypeLength || !type.All(IsTypeChar))
            throw new ArgumentException($"A binary message type is 1-{MaxTypeLength} printable ASCII characters, no spaces.", nameof(type));
        var frame = new byte[HeaderLength(type.Length) + payload.Length];
        frame[0] = (byte)type.Length;
        Encoding.ASCII.GetBytes(type, frame.AsSpan(1));
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(1 + type.Length), number);
        payload.CopyTo(frame.AsSpan(HeaderLength(type.Length)));
        return frame;
    }

    /// <summary>Reads the envelope. False for a frame too short for its header or a type that is not printable
    /// ASCII; <paramref name="payloadOffset"/> is where the game's bytes start.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out string type, out long number, out int payloadOffset)
    {
        type = "";
        number = 0;
        payloadOffset = 0;
        if (frame.Length < 1)
            return false;
        var length = frame[0];
        if (length is 0 or > MaxTypeLength || frame.Length < HeaderLength(length))
            return false;
        var name = frame.Slice(1, length);
        foreach (var b in name)
            if (!IsTypeChar((char)b))
                return false;
        type = Encoding.ASCII.GetString(name);
        number = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(1 + length));
        payloadOffset = HeaderLength(length);
        return true;
    }

    /// <summary>Printable ASCII without space: the one alphabet both directions accept.</summary>
    private static bool IsTypeChar(char c) => c is >= '!' and <= '~';
}
