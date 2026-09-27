namespace Burbuja.Core.Abstractions.Arm;

/// <summary>
/// The bytes a data-plane handler serves for a Download/Stream request. Returning <c>null</c>
/// from <see cref="IDataPlaneHandler.OpenAsync"/> means "not found" and the front door answers
/// 404 — it never substitutes an empty 200 stream.
/// </summary>
public sealed record DataPlanePayload(
    Stream Content,
    string ContentType,
    string? FileName = null,
    long? Length = null,
    string? ETag = null);

/// <summary>
/// Maps a resource to its event-stream id. A resource provider registers one of these so the
/// front door never hard-codes stream-id shapes.
/// </summary>
public interface IEventStreamResolver
{
    string Namespace { get; }

    bool TryResolve(ResourceId id, out string streamId);
}

/// <summary>
/// Serves the data-plane routes a resource provider declares (SSE is handled by
/// <see cref="IEventStreamResolver"/>; Download/Stream by this handler).
/// </summary>
public interface IDataPlaneHandler
{
    string Namespace { get; }

    ValueTask<DataPlanePayload?> OpenAsync(
        ResourceId id,
        string segment,
        DataPlaneKind kind,
        ArmPrincipal? principal,
        IReadOnlyDictionary<string, string> query,
        CancellationToken ct);
}
