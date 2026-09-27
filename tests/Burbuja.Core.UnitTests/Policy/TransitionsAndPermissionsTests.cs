using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.InMemory;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.Policy;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Policy;

/// <summary>
/// A5/A7 — permissions, named transitions, and the two tiers above a deny.
///
/// <para>The rules that matter are all about what an approval can and cannot reach: a forbidden
/// transition never opens one, a grant for one transition never satisfies another, a caller that
/// understates its own risk does not get the lower tier, and a request that did not declare what it
/// needs is clarified rather than judged.</para>
/// </summary>
public sealed class TransitionsAndPermissionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Contributor(params NamedTransition[] transitions) : ITransitionContributor
    {
        public IEnumerable<NamedTransition> Transitions => transitions;
    }

    private static PolicyContext Context(PolicyProfile profile) => new(
        StreamId: "s",
        WorkRoot: "/work/j",
        ArtifactDir: "/work/.artifacts/j",
        Profile: profile,
        TargetEnv: "dev",
        HostRepoPath: "/host",
        VerifyCommands: [],
        DevHosts: [],
        AllowedSecretIds: []);

    private static readonly NamedTransition ChangeMandate =
        new("CHANGE_MANDATE", RiskTier.Escalate, [Permission.ChangeMandate], Reason: "the mandate is the constitution");

    private static readonly NamedTransition EnableRealOrders =
        new("ENABLE_REAL_ORDERS", RiskTier.Forbidden, [Permission.MoveValue], Forbidden: true, Reason: "real money is out of scope, permanently");

    private static readonly NamedTransition PaperFill =
        new("PAPER_FILL", RiskTier.Gray, [Permission.Execute]);

    [Fact]
    public void A_transition_can_only_raise_the_floor()
    {
        var catalog = new TransitionCatalog([new Contributor(ChangeMandate, PaperFill, EnableRealOrders)]);

        // Policy computed Allow; the transition says Escalate. The higher wins.
        catalog.FloorFor("CHANGE_MANDATE", RiskTier.Allow).ShouldBe(RiskTier.Escalate);
        // Policy computed HardDeny; the transition's floor is only Gray. The higher still wins.
        catalog.FloorFor("PAPER_FILL", RiskTier.HardDeny).ShouldBe(RiskTier.HardDeny);
        // No transition named: the computed tier stands.
        catalog.FloorFor(null, RiskTier.Gray).ShouldBe(RiskTier.Gray);
        catalog.FloorFor("NOT_A_TRANSITION", RiskTier.Gray).ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void A_forbidden_transition_is_forbidden_whatever_policy_computed()
    {
        var catalog = new TransitionCatalog([new Contributor(EnableRealOrders)]);

        catalog.FloorFor("ENABLE_REAL_ORDERS", RiskTier.Allow).ShouldBe(RiskTier.Forbidden);
        // Even a deny is raised: forbidden is about what the host IS, not about how bad this instance is.
        catalog.FloorFor("ENABLE_REAL_ORDERS", RiskTier.HardDeny).ShouldBe(RiskTier.Forbidden);
    }

    [Fact]
    public void A_duplicate_transition_name_is_a_boot_failure()
    {
        // Two subsystems claiming one name for different floors is a configuration nobody can reason
        // about, and finding out at the moment somebody asks for that transition is finding out too late.
        var first = new Contributor(new NamedTransition("CHANGE_STRATEGY_MATERIAL", RiskTier.Escalate, [Permission.ChangeStrategy]));
        var second = new Contributor(new NamedTransition("CHANGE_STRATEGY_MATERIAL", RiskTier.Gray, [Permission.Propose]));

        var ex = Should.Throw<InvalidOperationException>(() => new TransitionCatalog([first, second]));
        ex.Message.ShouldContain("CHANGE_STRATEGY_MATERIAL");
        ex.Message.ShouldContain("twice");
    }

    [Fact]
    public void The_catalog_lists_every_contributed_transition()
    {
        var catalog = new TransitionCatalog([new Contributor(ChangeMandate, PaperFill), new Contributor(EnableRealOrders)]);
        catalog.All.Select(t => t.Name).ShouldBe(["CHANGE_MANDATE", "PAPER_FILL", "ENABLE_REAL_ORDERS"], ignoreOrder: true);
        catalog.TryGet("PAPER_FILL", out var found).ShouldBeTrue();
        found.Requires.ShouldBe([Permission.Execute]);
    }

    [Fact]
    public void Only_halt_is_always_permitted()
    {
        // A halt that needs permission is a request, not a halt — and the moment it is needed is the
        // moment the state machine is least willing to cooperate.
        EmergencyActions.AlwaysPermitted.ShouldBe([Permission.Halt]);
        EmergencyActions.IsEmergency([Permission.Halt]).ShouldBeTrue();
        EmergencyActions.IsEmergency([Permission.Halt, Permission.MoveValue]).ShouldBeFalse();
        EmergencyActions.IsEmergency([Permission.Resume]).ShouldBeFalse();
        EmergencyActions.ContainsEmergency([Permission.Read, Permission.Halt]).ShouldBeTrue();
    }

    /// <summary>
    /// A7's named test: a grant for one transition must never satisfy another. The two share a decision
    /// key here, which is the case that makes the check load-bearing — several named changes can arrive
    /// through the same tool, and matching on the key alone would hand out a permission nobody gave.
    /// </summary>
    [Fact]
    public void A_grant_for_one_transition_never_satisfies_another()
    {
        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryApprovalStore();
        var now = clock.UtcNow;

        store.PutGrant(new ApprovalGrant("Bash:write_prod", null, "s1", "write_prod", now.AddHours(4), "INCREASE_CAPITAL_OR_RISK"));

        store.FindGrant("Bash:write_prod", null, "INCREASE_CAPITAL_OR_RISK", now).ShouldNotBeNull();
        store.FindGrant("Bash:write_prod", null, "CHANGE_MANDATE", now).ShouldBeNull("a different transition is a different permission");
        store.FindGrant("Bash:write_prod", null, null, now).ShouldBeNull("an unnamed request is not covered by a named grant");
    }

    [Fact]
    public void An_unnamed_grant_does_not_satisfy_a_named_transition()
    {
        // "No name" is not a wildcard. Treating it as one would make every pre-A7 grant satisfy every A7
        // transition — a silent widening of permissions that no approval ever granted.
        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryApprovalStore();
        var now = clock.UtcNow;

        store.PutGrant(new ApprovalGrant("Bash:write_prod", null, "s1", "write_prod", now.AddHours(4)));
        store.FindGrant("Bash:write_prod", null, "INCREASE_CAPITAL_OR_RISK", now).ShouldBeNull();
        store.FindGrant("Bash:write_prod", null, null, now).ShouldNotBeNull();
    }

    [Fact]
    public void A_shape_grant_is_also_scoped_to_its_transition()
    {
        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryApprovalStore();
        var now = clock.UtcNow;

        store.PutGrant(new ApprovalGrant("", "bash:deploy", "s1", "shape:bash:deploy", now.AddHours(4), "CHANGE_STRATEGY_MATERIAL"));
        store.FindGrant("Bash:whatever", "bash:deploy", "CHANGE_STRATEGY_MATERIAL", now).ShouldNotBeNull();
        store.FindGrant("Bash:whatever", "bash:deploy", "PROMOTE_AUTONOMY", now).ShouldBeNull();
    }

    [Fact]
    public void The_tiers_are_ordered_and_the_new_ones_sit_above_a_deny()
    {
        // The ordering IS the lattice: `a > b` means "a is more severe". A new tier appended for
        // convenience rather than placed by severity would make IsMoreSevere lie.
        ((int)RiskTier.Forbidden).ShouldBeGreaterThan((int)RiskTier.HardDeny);
        ((int)RiskTier.Clarify).ShouldBeGreaterThan((int)RiskTier.Forbidden);
        Burbuja.Core.Sdk.Policy.PolicyLogic.IsMoreSevere(RiskTier.Forbidden, RiskTier.HardDeny).ShouldBeTrue();
        Burbuja.Core.Sdk.Policy.PolicyLogic.IsMoreSevere(RiskTier.Allow, RiskTier.Clarify).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_declaration_is_reported_with_the_exact_list()
    {
        // A clarify that said only "insufficient permissions" makes the caller guess, and guessing is what
        // produced the incomplete request. The finding carries the whole list.
        var finding = new PolicyFinding(
            RiskTier.Clarify,
            "missing_permissions",
            "action_request",
            "the request did not declare what it needs",
            Missing: [Permission.MoveValue, Permission.ChangeMandate]);

        finding.Missing.ShouldBe([Permission.MoveValue, Permission.ChangeMandate]);
        finding.Tier.ShouldBe(RiskTier.Clarify);
    }

    [Fact]
    public void A_request_that_declares_nothing_is_recognisable_as_such()
    {
        // An empty permission set is either a read or a caller that has not thought about it, and the
        // second is the dangerous one — so it is detectable rather than silently treated as safe.
        new ActionRequest("do it", RiskTier.Allow, [], "because").DeclaresNothing.ShouldBeTrue();
        new ActionRequest("read it", RiskTier.Allow, [Permission.Read], "because").DeclaresNothing.ShouldBeFalse();
    }

    /// <summary>
    /// The profile may soften an escalation. It must not touch a deny, a forbidden transition or a request
    /// that could not be judged.
    ///
    /// <para><b>What this fact does and does not prove, measured.</b> It proves the property: a terminal
    /// tier comes back unchanged from every profile. It does NOT prove the `&gt;=` in
    /// <c>ApplyProfile</c> is load-bearing — neutering that comparison to `==` leaves this fact green,
    /// because both branches below it are already unreachable for a tier above <c>HardDeny</c>. That is
    /// recorded here rather than left as an implied claim, and the guard keeps its form for the reason its
    /// comment gives.</para>
    /// </summary>
    [Theory]
    [InlineData(RiskTier.HardDeny)]
    [InlineData(RiskTier.Forbidden)]
    [InlineData(RiskTier.Clarify)]
    public void A_terminal_tier_survives_every_profile(RiskTier tier)
    {
        var finding = new PolicyFinding(tier, "whatever", "rule", "reason");
        var softened = PolicyEngine.ApplyProfile(finding, [], Context(PolicyProfile.FullAuto));

        softened.Tier.ShouldBe(tier);
    }

    [Fact]
    public void An_escalation_is_still_softened_by_the_full_auto_profile()
    {
        // The other direction, so the guard above is not simply "ApplyProfile does nothing".
        var finding = new PolicyFinding(RiskTier.Escalate, "external_write", "rule", "reason");
        PolicyEngine.ApplyProfile(finding, [], Context(PolicyProfile.FullAuto)).Tier.ShouldBe(RiskTier.Gray);
    }
}
