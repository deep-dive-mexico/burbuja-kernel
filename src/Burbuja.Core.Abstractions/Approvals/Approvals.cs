using System.Text.Json.Serialization;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Abstractions.Approvals;

public enum ApprovalKind
{
    Permission,
    Question,
    Confirmation,
    Escalation,
}

public sealed record ApprovalRequest(
    string StreamId,
    string? SubStreamId,
    ApprovalKind Kind,
    string SubjectType,
    string SubjectId,
    string DecisionKey,
    string? ShapeKey,
    string RiskClass,
    string Action,
    string? Preview,
    string Why,
    IReadOnlyList<string> Options,
    bool Grantable,
    TimeSpan Timeout,
    /// <summary>
    /// A7: the named transition this approval is for, when it is one.
    ///
    /// <para>It is part of the approval's IDENTITY, not a label on it. A grant minted for
    /// <c>INCREASE_CAPITAL_OR_RISK</c> must never satisfy <c>CHANGE_MANDATE</c>, however similar the two
    /// look at the call site — and a lookup that matched on the decision key alone would do exactly that
    /// whenever two transitions share a tool and a risk class, which is the normal case.</para>
    /// </summary>
    string? Transition = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Approved), "approved")]
[JsonDerivedType(typeof(Answered), "answered")]
[JsonDerivedType(typeof(Denied), "denied")]
[JsonDerivedType(typeof(TimedOut), "timeout")]
[JsonDerivedType(typeof(Cancelled), "cancelled")]
[JsonDerivedType(typeof(Deferred), "deferred")]
public abstract record ApprovalOutcome;

public sealed record Approved(bool Reused, string? Scope, string? Note) : ApprovalOutcome;

public sealed record Answered(string Answer) : ApprovalOutcome;

public sealed record Denied(string? Note) : ApprovalOutcome;

public sealed record TimedOut : ApprovalOutcome;

public sealed record Cancelled : ApprovalOutcome;

public sealed record Deferred : ApprovalOutcome;

public sealed record ApprovalRecord(
    string Id,
    ApprovalRequest Request,
    ApprovalOutcome? Outcome,
    DateTimeOffset OpenedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

public sealed record ApprovalQuery(
    string? SubscriptionId,
    string? SubjectType,
    string? SubjectId,
    string? StreamId,
    bool PendingOnly,
    int Top,
    string? ContinuationToken);

/// <summary>N4/C7: a reusable approval grant minted when a human approves with a scope.</summary>
public sealed record ApprovalGrant(
    string DecisionKey,
    string? ShapeKey,
    string StreamId,
    string? Scope,
    DateTimeOffset ExpiresAt,
    /// <summary>A7: the transition this grant authorises, and only that one.</summary>
    string? Transition = null);

/// <summary>
/// M7.2b: the grant-scope vocabulary, shared by the kernel, the Discord card and <c>pomni approve</c>.
///
/// <para>These are <b>wire strings</b>. A card rendered by the Discord plugin, a CLI invocation and the
/// kernel's grant table all have to agree on what "approve this and everything like it" means, so the
/// spelling lives in one place instead of being re-typed at each of the three.</para>
/// </summary>
public static class GrantScope
{
    /// <summary>Pre-approve every later command of this shape, not just this one.</summary>
    public const string ShapePrefix = "shape:";

    public static string Shape(string shapeKey) => ShapePrefix + shapeKey;

    /// <summary>True when <paramref name="scope"/> is exactly the shape-wide grant for this shape.</summary>
    public static bool IsShape(string? scope, string shapeKey) =>
        string.Equals(scope, ShapePrefix + shapeKey, StringComparison.Ordinal);
}

/// <summary>
/// The durable half of approvals: records and grants. The in-process waiters stay in
/// <see cref="IApprovalService"/>; this seam is what a database-backed store implements.
/// </summary>
public interface IApprovalStore
{
    ApprovalRecord? Get(string id);
    void Put(ApprovalRecord record);
    IReadOnlyCollection<ApprovalRecord> All();

    /// <summary>
    /// A grant that satisfies this request, or null.
    ///
    /// <para><paramref name="transition"/> is part of the match, not a hint: a stored grant that names a
    /// DIFFERENT transition must not satisfy this one. Two transitions can share a decision key (the key
    /// is derived from the tool and the risk class, and several named changes can arrive through the same
    /// tool), so matching without it hands out a permission nobody gave.</para>
    /// </summary>
    ApprovalGrant? FindGrant(string decisionKey, string? shapeKey, string? transition, DateTimeOffset now);
    void PutGrant(ApprovalGrant grant);
    void RemoveGrantsForStream(string streamId);
    void PurgeGrants(DateTimeOffset now);
}

/// <summary>
/// Approvals: open, wait, decide.
///
/// <para><b>Every decision carries its actor.</b> <paramref name="actorId"/> is who decided, and it used
/// to be the literal string <c>"human"</c> for all four verbs — so a Discord tap by the owner, a tap by
/// an allowed user and a raw ARM call were indistinguishable in the record, and a card that said
/// "decidido por X" was rendering a value nothing had ever set. Null still falls back to <c>"human"</c>
/// for a caller that genuinely has no principal (a test, a timer), but a surface that knows who tapped
/// must say so.</para>
/// </summary>
/// <summary>
/// M11 S1 — <b>decide only what you could have asked for.</b> One rule, in one place, for every path that
/// decides an approval card.
///
/// <para><b>The defect this replaces.</b> The approval verbs (<c>Burbuja.Resources/approvals/approve|deny|
/// answer</c>) were authorised as ordinary actions with no SCOPE: the AKOS User role carried them unscoped, and
/// approvals are kernel-wide, so a demo user could approve a <c>PAPER_FILL</c> the owner opened, a pomnibot job
/// card, or the parked tool call of its own persona — a human decision of record that was theirs. Nothing
/// recorded who decided either, because the verbs took no principal.</para>
///
/// <para><b>The rule.</b> Every opener passes the CANONICAL RESOURCE ID of whatever the card is about as its
/// <c>SubjectId</c>, and deciding it requires <c>{namespace}/{type}/write</c> AT THAT SCOPE — the same
/// permission that would let the decider have opened the thing in the first place. An Owner passes everything.
/// A user holding <c>Burbuja.Akos/turns/write</c> passes turn cards and nothing else. A subject that does not
/// parse as a resource id is refused for EVERYBODY, fail closed: "who may decide this" cannot be established,
/// and a card nobody can place is not a card anybody may settle.</para>
///
/// <para><b>Why an interface and not a handler detail.</b> There is more than one door onto a card —
/// <c>ApprovalHandler</c> for <c>Burbuja.Resources/approvals</c> and the Agents RP's
/// <c>workspaces/jobs/approvals</c> projection, which calls the service directly — and a rule enforced at one
/// door is a rule with a way around it. This lives in Abstractions so both doors can hold it, and the
/// implementation lives in the kernel where the evaluator does.</para>
/// </summary>
public interface IApprovalDecider
{
    /// <summary>
    /// The refusal, or null when <paramref name="principal"/> may decide <paramref name="record"/>.
    /// </summary>
    ValueTask<ArmError?> RefuseAsync(ApprovalRecord record, ArmPrincipal principal, CancellationToken ct);
}

public interface IApprovalService
{
    ValueTask<string> OpenAsync(ApprovalRequest request, CancellationToken ct);
    ValueTask<ApprovalOutcome> WaitAsync(string approvalId, CancellationToken ct);
    ValueTask<ApprovalOutcome> ApproveAsync(string approvalId, string? note, string? grantScope, double? grantHours, CancellationToken ct, string? actorId = null);
    ValueTask<ApprovalOutcome> DenyAsync(string approvalId, string? note, CancellationToken ct, string? actorId = null);
    ValueTask<ApprovalOutcome> AnswerAsync(string approvalId, string answer, CancellationToken ct, string? actorId = null);
    ValueTask<ApprovalOutcome> CancelAsync(string approvalId, CancellationToken ct, string? actorId = null);
    ValueTask<ApprovalRecord?> GetAsync(string id, CancellationToken ct);
    ValueTask<PagedResult<ApprovalRecord>> ListAsync(ApprovalQuery query, CancellationToken ct);
}
