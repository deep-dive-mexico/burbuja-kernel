using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Scopes;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Redaction;
using Burbuja.Core.Scopes;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Scopes;

/// <summary>
/// A6/A3 — the scope state machine and its halt.
///
/// <para>Every rule here is about what a halt CANNOT be undone by, and what an approval has to say
/// before it counts. A halt is a latch: no timer clears it, no promotion climbs out of it, and resuming
/// it takes an approval that names the right transition and a diagnosis of what went wrong.</para>
/// </summary>
public sealed class ScopeStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Ladder = ["observe", "paper", "limited", "autonomous"];

    private static async Task<(DurableScopeStore Store, InMemoryApprovalService Approvals, InMemoryEventLog Events)> BuildAsync(
        string scope = "mandate-1")
    {
        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryResourceStore(clock);
        var events = new InMemoryEventLog(new CaptureGroupRedactor(), clock);
        var approvals = new InMemoryApprovalService(
            clock,
            TimeProvider.System,
            events,
            Microsoft.Extensions.Options.Options.Create(new Burbuja.Core.Options.PolicyOptions()),
            new InMemoryApprovalStore());
        var scopes = new DurableScopeStore(approvals, store, events, clock);
        await scopes.EnsureAsync(scope, Ladder, Ct);
        return (scopes, approvals, events);
    }

    /// <summary>Open an approval carrying a given transition, and optionally approve it.</summary>
    private static async Task<string> ApprovedAsync(
        InMemoryApprovalService approvals,
        string transition,
        bool approve = true)
    {
        var id = await approvals.OpenAsync(
            new ApprovalRequest(
                "core.scope/mandate-1", null, ApprovalKind.Confirmation, "scope", "mandate-1",
                transition + ":mandate-1", null, "governance", transition, null, "asked",
                ["approve", "deny"], true, TimeSpan.FromHours(1), transition),
            Ct);
        if (approve)
            await approvals.ApproveAsync(id, "ok", null, null, Ct, actorId: "owner");
        return id;
    }

    [Fact]
    public async Task A_scope_starts_on_the_bottom_rung()
    {
        var (scopes, _, _) = await BuildAsync();

        var state = await scopes.GetAsync("mandate-1", Ct);

        state.Rung.ShouldBe("observe");
        state.Halted.ShouldBeFalse();
        state.AtBottom.ShouldBeTrue();
        state.Next.ShouldBe("paper");
    }

    [Fact]
    public async Task An_unknown_scope_resolves_to_null_rather_than_to_a_halted_one()
    {
        // "We have never seen this scope" and "this scope is stopped" are different facts, and the
        // resolver must not conflate them — one is a configuration gap, the other is an incident.
        var (scopes, _, _) = await BuildAsync();

        (await scopes.ResolveAsync("never-seen", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Re_basing_a_ladder_is_refused()
    {
        // Adopting a new ladder would move a scope's autonomy without an approval, which is the one thing
        // promotion exists to require.
        var (scopes, _, _) = await BuildAsync();

        var ex = await Should.ThrowAsync<ArmException>(async () =>
            await scopes.EnsureAsync("mandate-1", ["low", "high"], Ct));
        ex.Error.Message.ShouldContain("refusing to replace");
    }

    [Fact]
    public async Task A_halt_requires_evidence_a_reason_and_an_actor()
    {
        // "No containment without evidence." A halt that cannot name what it saw is indistinguishable
        // from a bug, and the check is here because a caller is what might forget it.
        var (scopes, _, _) = await BuildAsync();

        (await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", [], "phylax"), Ct))
            .Rejection!.ShouldContain("evidence");
        (await scopes.HaltAsync("mandate-1", new ScopeHalt("", ["ev-1"], "phylax"), Ct))
            .Rejection!.ShouldContain("reason");
        (await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], ""), Ct))
            .Rejection!.ShouldContain("actor");

        (await scopes.GetAsync("mandate-1", Ct)).Halted.ShouldBeFalse("a refused halt must not latch");
    }

    [Fact]
    public async Task A_halt_latches_and_announces()
    {
        var (scopes, _, events) = await BuildAsync();

        var halted = await scopes.HaltAsync("mandate-1", new ScopeHalt("drawdown 12 %", ["ledger:2026-09-13"], "phylax"), Ct);

        halted.Ok.ShouldBeTrue();
        halted.State.Halted.ShouldBeTrue();
        halted.State.HaltEvidence.ShouldBe(["ledger:2026-09-13"]);
        halted.State.HaltedBy.ShouldBe("phylax");

        var appended = await ReadAsync(events);
        appended.ShouldContain(e => e.Type == "core.scope.halted");
    }

    [Fact]
    public async Task Halting_twice_keeps_the_first_evidence()
    {
        // The later halt did not cause the stop. Overwriting the reason would replace the record of WHY
        // with the record of somebody noticing again.
        var (scopes, _, _) = await BuildAsync();
        await scopes.HaltAsync("mandate-1", new ScopeHalt("first cause", ["ev-1"], "phylax"), Ct);

        var again = await scopes.HaltAsync("mandate-1", new ScopeHalt("second cause", ["ev-2"], "someone"), Ct);

        again.Ok.ShouldBeTrue();
        again.State.HaltReason.ShouldBe("first cause");
        again.State.HaltEvidence.ShouldBe(["ev-1"]);
    }

    [Fact]
    public async Task A_resume_requires_the_right_transition_and_a_diagnosis()
    {
        var (scopes, approvals, _) = await BuildAsync();
        await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], "phylax"), Ct);

        // No diagnosis.
        (await scopes.ResumeAsync("mandate-1", new ScopeResume("", ""), Ct)).Rejection!.ShouldContain("diagnosis");

        // A diagnosis, but an approval for the WRONG transition.
        var wrong = await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy);
        (await scopes.ResumeAsync("mandate-1", new ScopeResume(wrong, "diag-1"), Ct))
            .Rejection!.ShouldContain(ScopeTransitions.ResumeFromHalt);

        // An approval that names the right transition but was never approved.
        var pending = await ApprovedAsync(approvals, ScopeTransitions.ResumeFromHalt, approve: false);
        (await scopes.ResumeAsync("mandate-1", new ScopeResume(pending, "diag-1"), Ct))
            .Rejection!.ShouldContain("approved");

        (await scopes.GetAsync("mandate-1", Ct)).Halted.ShouldBeTrue("nothing above may clear the latch");
    }

    [Fact]
    public async Task A_resume_with_the_right_approval_clears_the_latch()
    {
        var (scopes, approvals, events) = await BuildAsync();
        await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], "phylax"), Ct);
        var approval = await ApprovedAsync(approvals, ScopeTransitions.ResumeFromHalt);

        var resumed = await scopes.ResumeAsync("mandate-1", new ScopeResume(approval, "diag: the guard was inverted"), Ct);

        resumed.Ok.ShouldBeTrue();
        resumed.State.Halted.ShouldBeFalse();
        resumed.State.ResumeDiagnosis.ShouldBe("diag: the guard was inverted");
        (await ReadAsync(events)).ShouldContain(e => e.Type == "core.scope.resumed");
    }

    [Fact]
    public async Task A_resume_may_not_climb_the_ladder()
    {
        // Coming back from a halt after an incident is a different act from being granted more autonomy.
        // A resume that could climb would let a scope that just misbehaved advance by being un-halted.
        var (scopes, approvals, _) = await BuildAsync();
        await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], "phylax"), Ct);
        var approval = await ApprovedAsync(approvals, ScopeTransitions.ResumeFromHalt);

        var up = await scopes.ResumeAsync("mandate-1", new ScopeResume(approval, "diag", ToRung: "autonomous"), Ct);

        up.Ok.ShouldBeFalse();
        up.Rejection!.ShouldContain("may not promote");
        (await scopes.GetAsync("mandate-1", Ct)).Halted.ShouldBeTrue();
    }

    [Fact]
    public async Task A_resume_may_step_down()
    {
        var (scopes, approvals, _) = await BuildAsync();
        // Climb first, legitimately, then halt and come back lower.
        var promote = await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy);
        await scopes.PromoteAsync("mandate-1", new ScopePromotion(promote), Ct);
        await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], "phylax"), Ct);
        var resume = await ApprovedAsync(approvals, ScopeTransitions.ResumeFromHalt);

        var down = await scopes.ResumeAsync("mandate-1", new ScopeResume(resume, "diag", ToRung: "observe"), Ct);

        down.Ok.ShouldBeTrue();
        down.State.Rung.ShouldBe("observe");
    }

    [Fact]
    public async Task A_promotion_moves_exactly_one_rung()
    {
        var (scopes, approvals, _) = await BuildAsync();

        var first = await scopes.PromoteAsync("mandate-1", new ScopePromotion(await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy)), Ct);
        first.State.Rung.ShouldBe("paper");
        first.State.Promotions.ShouldBe(1);

        // A fresh approval is needed for each step: one approval authorises one change.
        var second = await scopes.PromoteAsync("mandate-1", new ScopePromotion(await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy)), Ct);
        second.State.Rung.ShouldBe("limited");
        second.State.Promotions.ShouldBe(2);
    }

    [Fact]
    public async Task A_promotion_is_refused_while_halted()
    {
        var (scopes, approvals, _) = await BuildAsync();
        await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], "phylax"), Ct);
        var approval = await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy);

        var promoted = await scopes.PromoteAsync("mandate-1", new ScopePromotion(approval), Ct);

        promoted.Ok.ShouldBeFalse();
        promoted.Rejection!.ShouldContain("halted");
        promoted.State.Rung.ShouldBe("observe");
    }

    [Fact]
    public async Task A_promotion_is_refused_by_an_unmet_condition_or_a_blocked_limit()
    {
        var (scopes, approvals, _) = await BuildAsync();

        var unmet = await scopes.PromoteAsync(
            "mandate-1", new ScopePromotion(await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy), Unmet: ["no rehearsal on paper"]), Ct);
        unmet.Rejection!.ShouldContain("unmet conditions");

        // A4 and A6 meet here: autonomy that cannot be bounded is not autonomy the host can grant.
        var blocked = await scopes.PromoteAsync(
            "mandate-1", new ScopePromotion(await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy), BlockedLimit: "committed_capital"), Ct);
        blocked.Rejection!.ShouldContain("no ceiling");
    }

    [Fact]
    public async Task A_promotion_off_the_top_of_the_ladder_is_refused()
    {
        var (scopes, approvals, _) = await BuildAsync();
        for (var i = 0; i < 3; i++)
            await scopes.PromoteAsync("mandate-1", new ScopePromotion(await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy)), Ct);

        (await scopes.GetAsync("mandate-1", Ct)).Rung.ShouldBe("autonomous");
        var beyond = await scopes.PromoteAsync("mandate-1", new ScopePromotion(await ApprovedAsync(approvals, ScopeTransitions.PromoteAutonomy)), Ct);

        beyond.Ok.ShouldBeFalse();
        beyond.Rejection!.ShouldContain("top rung");
    }

    [Fact]
    public async Task The_latch_survives_a_re_read()
    {
        // The whole point of a latch: it is in the store, not in the caller's memory.
        var (scopes, _, _) = await BuildAsync();
        await scopes.HaltAsync("mandate-1", new ScopeHalt("runaway", ["ev-1"], "phylax"), Ct);

        var resolver = (IScopeStateResolver)scopes;
        var resolved = await resolver.ResolveAsync("mandate-1", Ct);

        resolved.ShouldNotBeNull();
        resolved!.Halted.ShouldBeTrue();
        resolved.HaltReason.ShouldBe("runaway");
    }

    [Fact]
    public void A_halted_scope_allows_nothing_by_default()
    {
        // A halt that still allowed the class that caused it would not be a halt.
        var policy = new HaltedScopePolicy();
        policy.Allows("write_prod").ShouldBeFalse();
        policy.Allows("").ShouldBeFalse();

        var narrow = new HaltedScopePolicy(["read_only"]);
        narrow.Allows("read_only").ShouldBeTrue();
        narrow.Allows("write_prod").ShouldBeFalse();
    }

    private static async Task<List<LedgerEvent>> ReadAsync(InMemoryEventLog events)
    {
        var list = new List<LedgerEvent>();
        await foreach (var e in events.ReadRangeAsync("burbuja.resources/scopes/mandate-1", 0, long.MaxValue, 0, null, Ct))
            list.Add(e);
        return list;
    }
}
