using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Plugins.Policy.Core;

/// <summary>
/// A5: a declared <see cref="ActionRequest"/>, judged against the transition it names.
///
/// <para><b>This is what gives <see cref="RiskTier.Clarify"/> a producer.</b> A5/A7 added the tier and
/// <c>PolicyFinding.Missing</c>, and until now nothing emitted either — the tier existed so that "the
/// request cannot be judged, so it must not proceed" had somewhere to land, and a tier nothing reaches is
/// a comment rather than a rule.</para>
///
/// <para><b>Why a request is clarified rather than denied.</b> A deny says the caller asked for something
/// it may not have. A clarify says the caller did not finish writing the request: it named a transition it
/// does not declare the permissions for, or named one nobody defines. Those need different responses from
/// the caller — one is "no", the other is "say what you mean" — and collapsing them teaches a caller to
/// stop declaring anything, because an undeclared request and a refused one would look the same.</para>
///
/// <para><b>The missing list is exact and complete.</b> A clarify that said only "insufficient
/// permissions" makes the caller guess, and guessing is what produced the incomplete request in the first
/// place. The finding names every permission the transition requires and the request did not declare.</para>
///
/// <para><b>An unknown transition is not a free pass.</b> A host whose catalog has no contributors
/// answers every action request with a clarify naming the transition, because a request this host cannot
/// evaluate must not proceed — that is the same fail-closed rule the rest of the policy follows, and the
/// alternative (treat an unknown name as unconstrained) would make inventing a transition name a way
/// around the check.</para>
/// </summary>
public sealed class ActionRequestClassifier(TransitionCatalog catalog) : IPolicyClassifier
{
    /// <summary>The kind a declared action request arrives as.</summary>
    public const string Kind = "core.action_request";

    public bool Handles(string kind) => kind.Equals(Kind, StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        ActionRequest? request;
        try
        {
            request = subject.Input.Deserialize<ActionRequest>(JsonWire.Options);
        }
        catch (JsonException ex)
        {
            // A payload that will not parse is a request nobody can evaluate, which is a clarify rather
            // than a deny: the caller has a bug, and the answer should say so.
            return Verdict(Clarify($"The action request could not be read: {ex.Message}", []));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Action))
            return Verdict(Clarify("An action request must name the action it is asking about.", []));

        // A request that declares nothing is either a read or a caller that has not thought about it, and
        // the second is the dangerous one — so it is never silently treated as safe.
        if (request.Transition is not { Length: > 0 })
        {
            return Verdict(request.DeclaresNothing
                ? Clarify(
                    $"'{request.Action}' declares no permissions and names no transition, so there is nothing to judge it against.",
                    [])
                : Proceed(request, required: []));
        }

        if (!catalog.TryGet(request.Transition, out var transition))
        {
            return Verdict(Clarify(
                $"'{request.Transition}' is not a transition this host defines, so '{request.Action}' cannot be judged.",
                []));
        }

        // A forbidden transition never opens an approval, whatever the request declares. Checked before the
        // missing-permissions branch so a forbidden request is refused even when it is also incomplete: the
        // answer is "this host does not do that", not "fill in the form".
        if (transition.Forbidden)
        {
            return Verdict(new PolicyFinding(
                RiskTier.Forbidden,
                "forbidden_transition",
                request.Transition,
                transition.Reason ?? $"'{request.Transition}' is forbidden on this host."));
        }

        var missing = transition.Requires
            .Where(required => !request.RequiredPermissions.Contains(required))
            .ToList();
        if (missing.Count > 0)
        {
            return Verdict(Clarify(
                $"'{request.Transition}' requires {string.Join(", ", missing)}, which the request did not declare.",
                missing));
        }

        return Verdict(Proceed(request, transition.Requires));
    }

    /// <summary>
    /// The request declared what the transition needs. The tier is the more severe of what the caller
    /// CLAIMS and what the transition FLOORS AT — a caller that understates its own risk does not get the
    /// lower tier, which is the whole reason <see cref="ActionRequest.DeclaredRisk"/> is compared rather
    /// than trusted.
    ///
    /// <para><b>This method was written without the floor first, and a fact caught it.</b> The classifier
    /// returned the caller's own declaration, so a request declaring <c>Allow</c> for a transition that
    /// floors at <c>Escalate</c> came back allowed — the caller grading its own homework, which is the
    /// precise thing the floor exists to prevent. <see cref="TransitionCatalog.FloorFor"/> is the rule and
    /// it is now the one that is applied.</para>
    /// </summary>
    private PolicyFinding Proceed(ActionRequest request, IReadOnlyList<Permission> required) =>
        new(
            catalog.FloorFor(request.Transition, request.DeclaredRisk),
            "action_request",
            request.Transition ?? request.Action,
            required.Count == 0
                ? $"'{request.Action}' declares no permissions; nothing further is required of it."
                : $"'{request.Action}' declares {string.Join(", ", required)} and names the reason: {request.Reason}");

    private static PolicyFinding Clarify(string reason, IReadOnlyList<Permission> missing) =>
        new(RiskTier.Clarify, "missing_permissions", Kind, reason, Missing: missing);

    private static PolicyVerdict Verdict(PolicyFinding finding) => new(finding.Tier, finding, [finding]);
}
