using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;

namespace Burbuja.Core.Arm;

/// <summary>
/// Download/Stream over an <see cref="IArtifactStore"/>: the artifact key is the resource's
/// trimmed ARM id (lower-cased canonical, no leading slash) plus the data-plane sub-path
/// (…/jobs/j1/artifacts/patch.diff → &lt;job key&gt;/patch.diff).
/// </summary>
public abstract class ArtifactDataPlaneHandler(IArtifactStore artifacts) : IDataPlaneHandler
{
    public abstract string Namespace { get; }

    /// <summary>Used when the stored blob carries no content type.</summary>
    protected virtual string DefaultContentType => "application/octet-stream";

    public virtual string KeyFor(ResourceId id)
    {
        var baseKey = (id with { Action = null, DataPlaneSegment = null, DataPlanePath = null })
            .Key.Trim().TrimStart('/');
        var sub = id.DataPlanePath?.Trim().TrimStart('/');
        return string.IsNullOrEmpty(sub) ? baseKey : $"{baseKey}/{sub}";
    }

    public async ValueTask<DataPlanePayload?> OpenAsync(
        ResourceId id,
        string segment,
        DataPlaneKind kind,
        ArmPrincipal? principal,
        IReadOnlyDictionary<string, string> query,
        CancellationToken ct)
    {
        _ = segment;
        _ = kind;
        _ = principal;
        _ = query;
        var key = KeyFor(id);
        var blob = await artifacts.GetAsync(key, ct);
        if (blob is null)
            return null;

        var contentType = string.IsNullOrWhiteSpace(blob.ContentType) ? DefaultContentType : blob.ContentType;
        return new DataPlanePayload(blob.Content, contentType, Path.GetFileName(key), null, blob.Sha256);
    }
}
