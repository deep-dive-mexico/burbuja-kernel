using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Arm;

public sealed record IdempotencyRecord(
    string RequestHash,
    JsonNode? Response,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

/// <summary>
/// N4: the idempotency-key seam. <see cref="TryBegin"/> must be exclusive per key so two
/// concurrent identical writes cannot both execute.
/// </summary>
public interface IIdempotencyStore
{
    bool TryBegin(string key);
    void End(string key);
    IdempotencyRecord? Get(string key);
    void Put(string key, IdempotencyRecord record);
}
