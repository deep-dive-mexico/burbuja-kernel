using System.Text.Json;
using System.Text.Json.Serialization;

namespace Burbuja.Core.Abstractions.Arm;

public sealed class PutOutcomeJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PutOutcome<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var props = typeToConvert.GetGenericArguments()[0];
        var conv = typeof(PutOutcomeJsonConverter<>).MakeGenericType(props);
        return (JsonConverter)Activator.CreateInstance(conv)!;
    }
}

public sealed class PutOutcomeJsonConverter<TProps> : JsonConverter<PutOutcome<TProps>> where TProps : class
{
    private static string WireName(string name, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;

    private static JsonElement Prop(JsonElement root, string name, JsonSerializerOptions options)
    {
        var wire = WireName(name, options);
        if (root.TryGetProperty(wire, out var el) || root.TryGetProperty(name, out el))
            return el;
        throw new JsonException($"Missing property '{wire}'.");
    }

    public override PutOutcome<TProps> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = Prop(root, "kind", options).GetString();
        return kind switch
        {
            "created" => new PutOutcome<TProps>.Created(Prop(root, "resource", options).Deserialize<ResourceEnvelope<TProps>>(options)!),
            "updated" => new PutOutcome<TProps>.Updated(Prop(root, "resource", options).Deserialize<ResourceEnvelope<TProps>>(options)!),
            "accepted" => new PutOutcome<TProps>.Accepted(
                Prop(root, "resource", options).Deserialize<ResourceEnvelope<TProps>>(options)!,
                Prop(root, "operationId", options).GetString()!,
                ReadRetryAfter(Prop(root, "retryAfter", options), options)),
            "rejected" => new PutOutcome<TProps>.Rejected(Prop(root, "error", options).Deserialize<ArmError>(options)!),
            _ => throw new JsonException($"Unknown PutOutcome kind '{kind}'."),
        };
    }

    private static TimeSpan ReadRetryAfter(JsonElement el, JsonSerializerOptions options) =>
        el.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(el.GetDouble())
            : el.Deserialize<TimeSpan>(options);

    public override void Write(Utf8JsonWriter writer, PutOutcome<TProps> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case PutOutcome<TProps>.Created created:
                writer.WriteString(WireName("kind", options), "created");
                writer.WritePropertyName(WireName("resource", options));
                JsonSerializer.Serialize(writer, created.Resource, options);
                break;
            case PutOutcome<TProps>.Updated updated:
                writer.WriteString(WireName("kind", options), "updated");
                writer.WritePropertyName(WireName("resource", options));
                JsonSerializer.Serialize(writer, updated.Resource, options);
                break;
            case PutOutcome<TProps>.Accepted accepted:
                writer.WriteString(WireName("kind", options), "accepted");
                writer.WritePropertyName(WireName("resource", options));
                JsonSerializer.Serialize(writer, accepted.Resource, options);
                writer.WriteString(WireName("operationId", options), accepted.OperationId);
                writer.WriteNumber(WireName("retryAfter", options), (int)Math.Max(0, accepted.RetryAfter.TotalSeconds));
                break;
            case PutOutcome<TProps>.Rejected rejected:
                writer.WriteString(WireName("kind", options), "rejected");
                writer.WritePropertyName(WireName("error", options));
                JsonSerializer.Serialize(writer, rejected.Error, options);
                break;
            default:
                throw new JsonException(value.GetType().Name);
        }

        writer.WriteEndObject();
    }
}
