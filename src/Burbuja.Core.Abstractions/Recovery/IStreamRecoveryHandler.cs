namespace Burbuja.Core.Abstractions.Recovery;

/// <summary>
/// C7: what a recovery sweep should look for. A handler returns one of these to be swept by
/// indexed store queries; returning <c>null</c> opts out of sweeping entirely.
/// </summary>
public sealed record RecoveryQuery(
    string Namespace,
    string Type,
    IReadOnlyList<string> States,
    int PageSize = 200);

public interface IStreamRecoveryHandler
{
    string StreamType { get; }

    ValueTask RecoverAsync(string streamId, CancellationToken ct);

    /// <summary>Null (the default) means this handler is not swept.</summary>
    RecoveryQuery? Query => null;
}
