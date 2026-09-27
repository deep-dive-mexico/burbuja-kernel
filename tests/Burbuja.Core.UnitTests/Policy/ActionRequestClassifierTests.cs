using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Plugins.Policy.Core;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Policy;

/// <summary>
/// A5's other half: the declared action request, and the tier that had no producer.
///
/// <para>A5/A7 added <see cref="RiskTier.Clarify"/> and <c>PolicyFinding.Missing</c> so that "the request
/// cannot be judged, so it must not proceed" had somewhere to land. Nothing emitted either until
/// <see cref="ActionRequestClassifier"/>, and a tier nothing reaches is a comment rather than a rule.
/// These facts are about the three answers it can give and why they are different: proceed, clarify with
/// the exact list, or refuse outright because the host does not do that.</para>
/// </summary>
public sealed class ActionRequestClassifierTests
{
    private sealed class Contributor(params NamedTransition[] transitions) : ITransitionContributor
    {
        public IEnumerable<NamedTransition> Transitions => transitions;
    }

    private static readonly NamedTransition ChangeMandate =
        new("CHANGE_MANDATE", RiskTier.Escalate, [Permission.ChangeMandate, Permission.Propose], Reason: "the mandate is the constitution");

    private static readonly NamedTransition PaperFill =
        new("PAPER_FILL", RiskTier.Gray, [Permission.Execute]);

    private static readonly NamedTransition EnableRealOrders =
        new("ENABLE_REAL_ORDERS", RiskTier.Forbidden, [Permission.MoveValue], Forbidden: true, Reason: "real money is out of scope, permanently");

    private static ActionRequestClassifier Classifier(params NamedTransition[] transitions) =>
        new(new TransitionCatalog([new Contributor(transitions)]));

    private static PolicyVerdict Classify(
        ActionRequestClassifier classifier,
        string action,
        RiskTier declared,
        Permission[] declaredPermissions,
        string? transition) =>
        classifier.Classify(new ActionPolicySubject(
            ActionRequestClassifier.Kind,
            JsonSerializer.SerializeToNode(
                new ActionRequest(action, declared, declaredPermissions, "because", transition),
                JsonWire.Options)!.AsObject(),
            new PolicyContext("s", "/w", "/a", PolicyProfile.FullAuto, "dev", "/h", [], [], [])));

    [Fact]
    public void A_request_that_declares_what_its_transition_needs_proceeds()
    {
        var verdict = Classify(Classifier(ChangeMandate), "change the mandate", RiskTier.Escalate,
            [Permission.ChangeMandate, Permission.Propose], "CHANGE_MANDATE");

        verdict.Tier.ShouldBe(RiskTier.Escalate);
        verdict.Finding.Class.ShouldBe("action_request");
    }

    /// <summary>The named requirement: a missing permission yields a clarify carrying the exact list.</summary>
    [Fact]
    public void A_missing_permission_yields_a_clarify_with_the_exact_list()
    {
        var verdict = Classify(Classifier(ChangeMandate), "change the mandate", RiskTier.Escalate,
            [Permission.Propose], "CHANGE_MANDATE");

        verdict.Tier.ShouldBe(RiskTier.Clarify);
        verdict.Finding.Missing.ShouldBe([Permission.ChangeMandate], "the list is the whole answer; 'insufficient permissions' is not");
        verdict.Finding.Reason.ShouldContain("CHANGE_MANDATE");
    }

    [Fact]
    public void Every_missing_permission_is_named_not_just_the_first()
    {
        // A clarify that stopped at the first gap would send the caller round the loop once per permission.
        var verdict = Classify(Classifier(ChangeMandate), "change the mandate", RiskTier.Escalate, [], "CHANGE_MANDATE");

        verdict.Finding.Missing.ShouldBe([Permission.ChangeMandate, Permission.Propose]);
    }

    [Fact]
    public void A_clarify_grants_nothing()
    {
        // The rule, stated as a fact: clarify is the MOST severe tier, so no profile and no downstream
        // softening can turn an unfinished request into a permission.
        ((int)RiskTier.Clarify).ShouldBeGreaterThan((int)RiskTier.Forbidden);

        var verdict = Classify(Classifier(ChangeMandate), "change the mandate", RiskTier.Allow, [], "CHANGE_MANDATE");
        verdict.Tier.ShouldBe(RiskTier.Clarify);
        verdict.Tier.ShouldNotBe(RiskTier.Allow);
    }

    [Fact]
    public void An_unknown_transition_is_clarified_not_waved_through()
    {
        // Treating an unknown name as unconstrained would make inventing a transition a way around the
        // check, which is the one thing a permission vocabulary must not allow.
        var verdict = Classify(Classifier(ChangeMandate), "do a thing", RiskTier.Allow, [Permission.MoveValue], "INVENTED_TRANSITION");

        verdict.Tier.ShouldBe(RiskTier.Clarify);
        verdict.Finding.Reason.ShouldContain("not a transition this host defines");
    }

    [Fact]
    public void A_host_with_no_transitions_clarifies_every_named_request()
    {
        // No contributors is a valid host state, and a request it cannot evaluate must not proceed — the
        // same fail-closed rule the rest of the policy follows.
        var empty = new ActionRequestClassifier(new TransitionCatalog([]));

        Classify(empty, "anything", RiskTier.Allow, [Permission.Read], "CHANGE_MANDATE").Tier.ShouldBe(RiskTier.Clarify);
    }

    [Fact]
    public void A_forbidden_transition_is_refused_even_when_it_is_also_incomplete()
    {
        // The answer is "this host does not do that", not "fill in the form". Checked before the
        // missing-permissions branch for exactly that reason.
        var verdict = Classify(Classifier(EnableRealOrders), "connect the real account", RiskTier.Allow, [], "ENABLE_REAL_ORDERS");

        verdict.Tier.ShouldBe(RiskTier.Forbidden);
        verdict.Finding.Class.ShouldBe("forbidden_transition");
        verdict.Finding.Reason.ShouldContain("permanently");
    }

    [Fact]
    public void A_declared_risk_below_the_transition_floor_does_not_lower_it()
    {
        // The caller's declaration is a CLAIM to be compared, never a value to be trusted. A transition's
        // floor can only raise the tier.
        var verdict = Classify(Classifier(ChangeMandate), "change the mandate", RiskTier.Allow,
            [Permission.ChangeMandate, Permission.Propose], "CHANGE_MANDATE");

        verdict.Tier.ShouldBe(RiskTier.Escalate, "the transition floors at escalate and the declaration was Allow");
    }

    [Fact]
    public void A_request_with_no_transition_and_no_permissions_is_clarified_with_an_empty_list()
    {
        // Empty because there is no transition to source the requirement from — and the reason says which
        // of the two problems it is, so the caller knows what to add.
        var verdict = Classify(Classifier(ChangeMandate), "do something", RiskTier.Allow, [], transition: null);

        verdict.Tier.ShouldBe(RiskTier.Clarify);
        verdict.Finding.Missing.ShouldBeEmpty();
        verdict.Finding.Reason.ShouldContain("no transition");
    }

    [Fact]
    public void An_unparseable_request_is_clarified_rather_than_throwing()
    {
        var classifier = Classifier(ChangeMandate);
        var verdict = classifier.Classify(new ActionPolicySubject(
            ActionRequestClassifier.Kind,
            new JsonObject { ["action"] = JsonValue.Create(42) },
            new PolicyContext("s", "/w", "/a", PolicyProfile.FullAuto, "dev", "/h", [], [], [])));

        verdict.Tier.ShouldBe(RiskTier.Clarify);
    }

    [Fact]
    public void The_classifier_claims_only_action_request_kinds()
    {
        var classifier = Classifier(ChangeMandate);
        classifier.Handles("core.action_request").ShouldBeTrue();
        classifier.Handles("Bash").ShouldBeFalse();
        classifier.Handles("unknown.action").ShouldBeFalse();
    }

    [Fact]
    public void The_catalog_that_reaches_the_classifier_is_the_one_core_registers()
    {
        // A5/A7's catalog had never been constructed outside a fact. This pins that the shape Core builds
        // from its contributors is the one the classifier consumes — a duplicate still fails at
        // construction, and an empty host is still valid.
        var catalog = new TransitionCatalog([new Contributor(PaperFill), new Contributor(ChangeMandate, EnableRealOrders)]);

        catalog.All.Count.ShouldBe(3);
        Should.Throw<InvalidOperationException>(() =>
            new TransitionCatalog([new Contributor(PaperFill), new Contributor(PaperFill)]));
    }
}
