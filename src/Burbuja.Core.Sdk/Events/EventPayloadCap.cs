using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Artifacts;

namespace Burbuja.Core.Events;

/// <summary>
/// N6: keeps oversized event payloads out of the event log. A payload over the cap is written to
/// the artifact store and replaced by a <c>{"$spill": {artifactKey, bytes, sha256}}</c> stub, so
/// the log stays small and the original bytes are retrievable and verifiable.
/// </summary>
public static class EventPayloadCap
{
    public const int DefaultMaxBytes = 8192;

    public static string SpillKey(string streamId, long seq) =>
        $"events/{Sanitize(streamId)}/{seq}.json";

    public static async ValueTask<JsonNode?> TrySpillAsync(
        JsonNode? payload,
        string streamId,
        long seq,
        int maxBytes,
        IArtifactStore artifacts,
        CancellationToken ct)
    {
        if (payload is null)
            return null;

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        if (maxBytes <= 0 || bytes.Length <= maxBytes)
            return payload.DeepClone();

        var key = SpillKey(streamId, seq);
        await artifacts.PutAsync(key, new MemoryStream(bytes, writable: false), "application/json", null, ct);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new JsonObject
        {
            ["$spill"] = new JsonObject
            {
                ["artifactKey"] = key,
                ["bytes"] = bytes.Length,
                ["sha256"] = sha256,
            },
        };
    }

    private static string Sanitize(string streamId)
    {
        var chars = streamId.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "stream" : s;
    }
}
