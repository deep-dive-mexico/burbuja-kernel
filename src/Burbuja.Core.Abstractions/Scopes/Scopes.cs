using Burbuja.Core.Abstractions.Approvals;

namespace Burbuja.Core.Abstractions.Scopes;

/// <summary>
/// A6: where a scope stands — how much autonomy it has been granted, and whether it is halted.
///
/// <para><b>The rung names are DATA, not an enum.</b> The kernel has no vocabulary for what a scope's
/// levels mean; a resource provider supplies the ladder as a list of names and this type only records
/// which one is current and that promotion moves one step at a time. That is the dividing question again:
/// the mechanism is Core's, the words are the domain's.</para>
///
/// <para><b>Halted is a state, not a rung.</b> A halt is not "the lowest rung" — it is a latch that
/// outlives whatever caused it, and resuming is a different act from promoting. Collapsing them would
/// mean a scope could climb out of a halt by being promoted, which is the opposite of what a halt is
/// for.</para>
/// </summary>
/// <param name="Rung">The rung the scope is on, by name.</param>
/// <param name="Ladder">The ladder's rungs, lowest first. Data, supplied by the caller that ensures the scope.</param>
/// <param name="Halted">The latch. Set by <c>Halt</c>, cleared only by an approved <c>Resume</c>.</param>
/// <param name="HaltEvidence">What the halt was based on. At least one, always — see <see cref="ScopeHalt"/>.</param>
public sealed record ScopeState(
    string Scope,
    string Rung,
    IReadOnlyList<string> Ladder,
    bool Halted = false,
    string? HaltReason = null,
    IReadOnlyList<string>? HaltEvidence = null,
    DateTimeOffset? HaltedAt = null,
    string? HaltedBy = null,
    string? ResumeDiagnosis = null,
    DateTimeOffset? ResumedAt = null,
    int Promotions = 0)
{
    /// <summary>True when the scope has a ladder and is on its first rung.</summary>
    public bool AtBottom => Ladder.Count == 0 || string.Equals(Rung, Ladder[0], StringComparison.Ordinal);

    /// <summary>Where the current rung sits, or -1 when it is not on the ladder (an empty one).</summary>
    public int RungIndex => IndexOfRung(Rung);

    private int IndexOfRung(string rung)
    {
        var wanted = rung;
        for (var i = 0; i < Ladder.Count; i++)
        {
            if (string.Equals(Ladder[i], wanted, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>The next rung up, or null at the top.</summary>
    public string? Next
    {
        get
        {
            var index = RungIndex;
            return index < 0 || index + 1 >= Ladder.Count ? null : Ladder[index + 1];
        }
    }

    /// <summary>True when <paramref name="rung"/> names a rung of this ladder.</summary>
    public bool HasRung(string rung) => IndexOfRung(rung) >= 0;

    /// <summary>Where the current rung sits, by name, or -1.</summary>
    public int RungIndexOf(string rung) => IndexOfRung(rung);
}

/// <summary>
/// A6: why a scope is being halted, by whom, and on what evidence.
///
/// <para><b><see cref="EvidenceRefs"/> is required and must not be empty.</b> "No containment without
/// evidence" is a rule the design states explicitly: a halt that cannot name what it saw is
/// indistinguishable from a bug, and the first thing anybody asks afterwards is what the evidence was.
/// The check is here rather than in a caller because a caller is exactly what might forget.</para>
/// </summary>
public sealed record ScopeHalt(
    string Reason,
    IReadOnlyList<string> EvidenceRefs,
    string Actor,
    DateTimeOffset? At = null);

/// <summary>
/// A3: the conditions for resuming. Both the approval and the diagnosis are required, and
/// <see cref="ToRung"/> may only go DOWN the ladder.
///
/// <para><b>A resume that can promote is a promotion with extra steps.</b> Coming back from a halt after
/// an incident is a different act from being granted more autonomy, and allowing the resume path to raise
/// the rung would let a scope that just misbehaved climb by being un-halted. The rule is one line and it
/// is the one that keeps the two acts separate.</para>
/// </summary>
public sealed record ScopeResume(
    string ApprovalId,
    string DiagnosisRef,
    string? ToRung = null,
    string? Actor = null,
    DateTimeOffset? At = null);

/// <summary>A3: one rung up, and only one, with nothing outstanding.</summary>
/// <param name="ApprovalId">The approval whose transition is <c>PROMOTE_AUTONOMY</c>.</param>
/// <param name="Unmet">Conditions the caller knows are unmet. Any entry refuses the promotion.</param>
/// <param name="BlockedLimit">A dimension the scope has no ceiling for, if any — a blocked limit refuses it.</param>
public sealed record ScopePromotion(
    string ApprovalId,
    IReadOnlyList<string>? Unmet = null,
    string? BlockedLimit = null,
    string? Actor = null,
    DateTimeOffset? At = null);

/// <summary>
/// The transitions a scope's state machine is allowed to be driven by. These are the names an approval
/// must carry: an approval for anything else does not clear a halt and does not promote a scope, however
/// approving it was.
/// </summary>
public static class ScopeTransitions
{
    public const string ResumeFromHalt = "RESUME_FROM_HALT";
    public const string PromoteAutonomy = "PROMOTE_AUTONOMY";
}

/// <summary>What a state change did, and why it did not when it did not.</summary>
public sealed record ScopeChange(bool Ok, ScopeState State, string? Rejection = null)
{
    public static ScopeChange Refused(ScopeState state, string reason) => new(false, state, reason);
}

/// <summary>
/// A6: the scope state machine, over <c>Burbuja.Resources/scopes/{scope}</c>.
///
/// <para><b>Every change is one compare-and-set write.</b> "100 % ⇒ HALTED" has to be a single write or
/// the store can be found holding a breach with no halt — which is the state a limit exists to prevent.
/// The same applies to the latch itself: a halt that took two writes could be half-applied.</para>
///
/// <para><b>Resuming and promoting are human acts.</b> Both take an approval id, and the approval must
/// carry the matching transition. There is no timer, no rollover and no automatic clear: a day boundary
/// that could unlatch a halt would make "halted" mean "halted until tomorrow".</para>
/// </summary>
public interface IScopeStateStore
{
    /// <summary>Read a scope, or a state with no ladder when it has never been seen.</summary>
    ValueTask<ScopeState> GetAsync(string scope, CancellationToken ct);

    /// <summary>Create the scope on the first rung if it does not exist; never rewrites an existing ladder.</summary>
    ValueTask<ScopeState> EnsureAsync(string scope, IReadOnlyList<string> ladder, CancellationToken ct);

    /// <summary>Latch the halt. Requires a reason, at least one evidence reference, and an actor.</summary>
    ValueTask<ScopeChange> HaltAsync(string scope, ScopeHalt halt, CancellationToken ct);

    /// <summary>
    /// Clear the halt. Requires an approved <c>RESUME_FROM_HALT</c> and a diagnosis, and may only move the
    /// rung DOWN.
    /// </summary>
    ValueTask<ScopeChange> ResumeAsync(string scope, ScopeResume resume, CancellationToken ct);

    /// <summary>
    /// Move up exactly one rung. Requires an approved <c>PROMOTE_AUTONOMY</c>, no unmet conditions, no
    /// blocked limit, and a scope that is not halted.
    /// </summary>
    ValueTask<ScopeChange> PromoteAsync(string scope, ScopePromotion promote, CancellationToken ct);
}

/// <summary>
/// A3: reads the scope's halted latch into the policy context, so every classifier sees it.
///
/// <para>It is a seam rather than a direct dependency because policy evaluation is synchronous and hot,
/// while the state lives in a resource: a resolver is asked once per subject and may cache, and a policy
/// pipeline that reached for the store on every classification would make the gate's latency a function
/// of the resource store.</para>
/// </summary>
public interface IScopeStateResolver
{
    /// <summary>The scope's state, or null when the scope is unknown (which is not the same as halted).</summary>
    ValueTask<ScopeState?> ResolveAsync(string scope, CancellationToken ct);
}

/// <summary>What a halted scope still permits.</summary>
/// <param name="AllowedClasses">
/// The risk classes that survive a halt. Empty by default, and that default is the whole point: a halt
/// that still allowed the class that caused it would not be a halt. The emergency permission
/// (<see cref="Burbuja.Core.Abstractions.Policy.Permission.Halt"/>) is exempt regardless, because
/// halting is idempotent and needing permission to stop is not stopping.
/// </param>
public sealed record HaltedScopePolicy(IReadOnlyList<string>? AllowedClasses = null)
{
    public bool Allows(string riskClass) =>
        AllowedClasses is { Count: > 0 } allowed
        && allowed.Contains(riskClass, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The approval check both human acts share. Kept in one place so resuming and promoting cannot drift
/// apart in how strictly they verify.
/// </summary>
public static class ScopeApprovalCheck
{
    /// <summary>
    /// True when the approval exists, is approved, and carries <paramref name="transition"/>. An approval
    /// for a different transition does NOT count, for the same reason a grant for one transition does not
    /// satisfy another: the two changes are different acts and one approval authorises one of them.
    /// </summary>
    public static async ValueTask<bool> IsApprovedAsync(
        IApprovalService approvals,
        string approvalId,
        string transition,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            return false;

        var record = await approvals.GetAsync(approvalId, ct).ConfigureAwait(false);
        if (record is null)
            return false;
        if (!string.Equals(record.Request.Transition ?? "", transition, StringComparison.Ordinal))
            return false;
        return record.Outcome is Approved;
    }
}
