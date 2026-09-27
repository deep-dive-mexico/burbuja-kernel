namespace Burbuja.Core.Abstractions.Arm;

/// <summary>
/// One ARM request, as an operator asks about it afterwards: who, what, where, how long, and how it ended.
///
/// <para><b>K2: this is telemetry, not the audit.</b> The audit of a decision already exists and is
/// per-resource: the intent and decision events on a scope's stream, the approval record, the limit ledger.
/// What no surface answered was the request-level question — "what has this host been asked to do in the
/// last hour, and by whom" — because a request refused by RBAC never reaches a handler and so never writes
/// an event anywhere. That gap is what this row fills.</para>
///
/// <para><b>Why a ring and not the event log.</b> A row per request on a resource stream would drown the
/// streams people read: the portal alone polls several collections a second. So the activity list is
/// deliberately bounded and volatile, and the durable record stays where it was — on the resource whose
/// thing changed. Losing the oldest activity rows loses history; losing an audit event would lose a
/// decision, and the two are not the same trade.</para>
/// </summary>
/// <param name="Status">The HTTP status the caller was answered with, after every middleware has run.</param>
/// <param name="ErrorCode">The ARM error code, for a request that was refused.</param>
public sealed record ArmActivityRow(
    DateTimeOffset At,
    double DurationMs,
    string Method,
    string Path,
    string? ResourceId,
    string? PrincipalId,
    string? PrincipalType,
    string? ApiVersion,
    int Status,
    string? ErrorCode,
    string? RequestId,
    string? CorrelationId,
    bool IsDataPlane);

/// <summary>
/// The last N requests this host served, newest last.
///
/// <para><b><see cref="Total"/> exists so "recent" cannot be mistaken for "all".</b> A reader that sees
/// 500 rows has no way to know whether the host served 500 requests or a million, and "the ring is full"
/// and "nothing else happened" look identical without a counter.</para>
/// </summary>
public interface IArmActivityLog
{
    void Record(ArmActivityRow row);

    /// <summary>The rows still held, oldest first, optionally filtered. Bounded by the ring, never by the caller.</summary>
    IReadOnlyList<ArmActivityRow> Recent(string? principalId = null, DateTimeOffset? since = null, int limit = 200);

    /// <summary>Every request this process has recorded, including the ones the ring has already dropped.</summary>
    long Total { get; }

    /// <summary>How many rows the ring holds at most. Reported so a reader can tell a full ring from a short history.</summary>
    int Capacity { get; }
}
