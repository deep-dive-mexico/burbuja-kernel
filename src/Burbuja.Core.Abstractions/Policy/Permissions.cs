namespace Burbuja.Core.Abstractions.Policy;

/// <summary>
/// A5: what a caller is asking to be allowed to do, expressed as capabilities rather than as a tool name.
///
/// <para><b>Why a permission vocabulary exists at all.</b> A tool name is an implementation detail: a
/// rename, a new wrapper or a different integration surface changes it, and any rule keyed on it silently
/// stops applying. Permissions are the small, stable set of things the host can actually do to the world,
/// so a rule written against one survives the tool being replaced — and a caller can be required to say
/// what it wants before it is judged.</para>
///
/// <para><b>Deliberately generic.</b> Nothing here names a market, an instrument or a currency. The
/// vocabulary is the kernel's; the words for what a particular resource provider does with it belong to
/// that provider (the same dividing question as the rest of M8).</para>
/// </summary>
public enum Permission
{
    /// <summary>Look at state. The only permission that is safe to grant broadly.</summary>
    Read = 0,

    /// <summary>Put a proposal in front of a human without changing anything.</summary>
    Propose = 1,

    /// <summary>Perform the ordinary operation of the domain.</summary>
    Execute = 2,

    /// <summary>Move value — money, quota, an allocation — out of the host's control.</summary>
    MoveValue = 3,

    /// <summary>Change how the domain decides, not just what it does this time.</summary>
    ChangeStrategy = 4,

    /// <summary>Change the mandate: what the domain is allowed to do at all.</summary>
    ChangeMandate = 5,

    /// <summary>Stop. Always available, to anybody, in any state — see <see cref="EmergencyActions"/>.</summary>
    Halt = 6,

    /// <summary>Start again after a halt. Human-only in practice, and never implied by <see cref="Halt"/>.</summary>
    Resume = 7,
}

/// <summary>
/// A5: what a caller is asking for, as a gate subject.
///
/// <para><b><see cref="DeclaredRisk"/> is a claim, not a fact.</b> The caller says how risky it thinks its
/// own request is, and the policy computes the risk itself — the declaration exists so the two can be
/// COMPARED. A caller that understates the risk of what it is about to do is the case this field was
/// added for: the computed tier wins, and the disagreement is recorded rather than resolved silently in
/// the caller's favour.</para>
///
/// <para><b><see cref="RequiredPermissions"/> is never empty in a well-formed request.</b> A request that
/// says it needs nothing is either a read or a caller that has not thought about it; the second is the
/// dangerous one, so an empty set is treated as a missing declaration rather than as "safe".</para>
/// </summary>
public sealed record ActionRequest(
    string Action,
    RiskTier DeclaredRisk,
    IReadOnlyList<Permission> RequiredPermissions,
    string Reason,
    /// <summary>The named transition this request is an instance of, when it is one.</summary>
    string? Transition = null)
{
    public bool DeclaresNothing => RequiredPermissions.Count == 0;
}

/// <summary>
/// A7: a change big enough to have a name, a floor and a list of what it needs.
///
/// <para><b>The floor is a minimum, not a default.</b> A transition's <see cref="Floor"/> is the tier it
/// cannot be treated as being below, whatever a classifier computes: a mandate change is a mandate change
/// even when it arrives disguised as a small edit. Policy takes the more severe of the two.</para>
///
/// <para><b><see cref="Forbidden"/> is not "needs the owner".</b> A forbidden transition never opens an
/// approval at all — there is no tap, no grant and no timeout that reaches it. That is the difference
/// between a thing this host will not do and a thing a human may authorise, and collapsing the two is how
/// a "the owner can always approve" escape hatch quietly becomes "the owner can always approve
/// anything".</para>
/// </summary>
public sealed record NamedTransition(
    string Name,
    RiskTier Floor,
    IReadOnlyList<Permission> Requires,
    bool Forbidden = false,
    string? Reason = null);

/// <summary>
/// A7: a source of named transitions. Contributors are collected at boot and their names must be unique —
/// two subsystems claiming <c>PROMOTE_AUTONOMY</c> for different things would make the name meaningless
/// exactly where it is being relied upon.
/// </summary>
public interface ITransitionContributor
{
    IEnumerable<NamedTransition> Transitions { get; }
}

/// <summary>The transitions this host knows, resolved once and validated.</summary>
public sealed class TransitionCatalog
{
    private readonly Dictionary<string, NamedTransition> _byName = new(StringComparer.Ordinal);

    public TransitionCatalog(IEnumerable<ITransitionContributor> contributors)
    {
        foreach (var contributor in contributors)
        {
            foreach (var transition in contributor.Transitions)
            {
                if (transition.Name.Length == 0)
                    throw new InvalidOperationException("a named transition must have a name");

                // A duplicate is a BOOT FAILURE, not a last-one-wins. Two subsystems claiming the same
                // name for different floors or different permissions is a configuration that cannot be
                // reasoned about, and discovering it at the moment somebody asks for that transition is
                // discovering it too late.
                if (!_byName.TryAdd(transition.Name, transition))
                {
                    throw new InvalidOperationException(
                        $"the transition '{transition.Name}' is contributed twice; "
                        + "a name must mean one thing or it cannot be the thing an approval is scoped to");
                }
            }
        }
    }

    public IReadOnlyCollection<NamedTransition> All => _byName.Values;

    public bool TryGet(string name, out NamedTransition transition) => _byName.TryGetValue(name, out transition!);

    /// <summary>
    /// The effective floor for a request: the more severe of what policy computed and what the transition
    /// declares. A transition's floor can only ever RAISE it.
    /// </summary>
    public RiskTier FloorFor(string? transitionName, RiskTier computed)
    {
        if (transitionName is not { Length: > 0 } || !_byName.TryGetValue(transitionName, out var transition))
            return computed;
        return transition.Forbidden ? RiskTier.Forbidden : (RiskTier)Math.Max((int)computed, (int)transition.Floor);
    }
}

/// <summary>
/// A5: the actions that are permitted in every state, including a halted scope and a breached limit.
///
/// <para><b>This is the one set that must not be gated.</b> A halt that requires permission to invoke is
/// not a halt — it is a request, and the moment it is most needed is the moment the state machine is
/// least willing to cooperate. So stopping is always allowed, to anybody, and the fact that it is the
/// only thing in this set is the point: adding <c>Resume</c> here would turn "stop everything" into "stop
/// everything, and let whatever just went wrong start itself again".</para>
/// </summary>
public static class EmergencyActions
{
    public static readonly IReadOnlySet<Permission> AlwaysPermitted = new HashSet<Permission> { Permission.Halt };

    /// <summary>True when a request is an emergency action and must bypass every state check.</summary>
    public static bool IsEmergency(IReadOnlyList<Permission> permissions) =>
        permissions.Count == 1 && permissions[0] == Permission.Halt;

    /// <summary>True when any permission the request names is emergency-permitted.</summary>
    public static bool ContainsEmergency(IReadOnlyList<Permission> permissions) =>
        permissions.Any(AlwaysPermitted.Contains);
}
