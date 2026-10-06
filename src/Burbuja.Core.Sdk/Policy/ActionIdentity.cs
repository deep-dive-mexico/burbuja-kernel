using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Burbuja.Core.Sdk.Policy;

/// <summary>Executable identity is separate from its human preview. Callers remove protocol retry metadata before crossing this seam.</summary>
public static class ActionIdentity
{
    public static string Fingerprint(string tool, JsonNode input, string? workRoot = null)
    {
        var body = new JsonObject
        {
            ["tool"] = tool,
            ["workRoot"] = workRoot,
            ["input"] = Canonical(input),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.ToJsonString())));
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(value => Canonical(value)).ToArray()),
        _ => node?.DeepClone(),
    };
}
