namespace Burbuja.Core.Abstractions.Artifacts;

public sealed record ArtifactBlob(string Key, Stream Content, string ContentType, string? Sha256, IReadOnlyDictionary<string, string> Metadata);

public interface IArtifactStore
{
    ValueTask PutAsync(string key, Stream content, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct);
    ValueTask<ArtifactBlob?> GetAsync(string key, CancellationToken ct);
    ValueTask<bool> ExistsAsync(string key, CancellationToken ct);
}
