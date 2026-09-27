using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

/// <summary>
/// M11 S1 — the rule that decides who may decide a card. See <see cref="IApprovalDecider"/> for why it exists
/// and what it replaced.
///
/// <para><b>Why the scope is the SUBJECT and not the approval resource.</b> Every card already lives under
/// <c>Burbuja.Resources/approvals/{id}</c>, which nobody holds a scope on — so authorising the verb against
/// the approval's own path either refuses every non-owner or, with an unscoped grant, allows everything. The
/// subject is the thing the card is ABOUT, and it is the only scope that carries the permission the question
/// is really asking: <em>could this person have opened this work themselves?</em></para>
/// </summary>
/// <para><b>Why it does NOT take the routing table.</b> It took one at first, and every AKOS fact in the tree
/// hung: <c>ArmRoutingTableBuilder.Build</c> constructs the resource handlers, so building the table constructs
/// <c>ApprovalHandler</c>, which asked for this, which asked for the table that was still being built. The
/// no-resolver <c>ResourceId.TryParse</c> is the right tool anyway — the question here is "what resource does
/// this canonical path name", which is a property of the path, not of which types a host has declared. A
/// subject naming a type this host does not have is still a subject with a scope, and its writability is
/// exactly what should be evaluated.</para>
/// </summary>
public sealed class ApprovalDecider(AuthorizationEvaluator evaluator) : IApprovalDecider
{
    public async ValueTask<ArmError?> RefuseAsync(
        ApprovalRecord record, ArmPrincipal principal, CancellationToken ct)
    {
        var subject = record.Request.SubjectId ?? "";

        // Fail closed. A subject that is not a resource id is a card whose owner cannot be established, and
        // "cannot be established" is never a licence — the same rule the policy gate applies to a request it
        // cannot judge. This also makes the requirement on OPENERS enforceable rather than advisory: an opener
        // that passes a bare name produces a card nobody can settle, which is loud and immediate.
        if (!ResourceId.TryParse(subject, out var id, out _) || id.Namespace.Length == 0)
        {
            return ArmError.Forbidden(
                $"The approval's subject '{subject}' is not a resource id, so who may decide it cannot be "
                + "established. An opener must pass the canonical id of the resource the card is about — a bare "
                + "name is a card nobody can settle, and it is refused for everybody rather than for nobody.");
        }

        var action = $"{id.Namespace}/{id.FullyQualifiedType}/write";
        var result = await evaluator.AuthorizeAsync(principal, id.Canonical, action, isDataAction: false, ct)
            .ConfigureAwait(false);

        return result.Allowed
            ? null
            : ArmError.Forbidden(
                $"'{principal.PrincipalId}' may not decide an approval about {id.Canonical}: that needs "
                + $"'{action}' at a scope covering it. A card is decidable by whoever could have opened the thing "
                + "it is about, and by nobody else.");
    }
}
