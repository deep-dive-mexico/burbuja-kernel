using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Sdk.Authorization;

namespace Burbuja.Portal.Services;

/// <summary>
/// The portal's RBAC pre-check: it answers "would the kernel let this principal do X" so a page can
/// hide a control it knows will be refused. It is *not* a security boundary — every write still
/// travels through ARM, which re-evaluates against the real assignments and role definitions.
///
/// It uses the shared <see cref="ArmActionMatcher"/> and the same role vocabulary the kernel uses:
/// the three built-in roles are mirrored here (the kernel's copy lives in
/// <c>Burbuja.Core/Arm/AuthorizationEvaluator.cs</c>, which the portal cannot reference) plus every
/// role contributed by a plugin through <see cref="IRoleContributor"/>.
/// </summary>
public sealed class PortalPermissions
{
    private readonly IReadOnlyDictionary<string, RoleDefinition> _definitions;

    private PortalPermissions(IReadOnlyDictionary<string, RoleDefinition> definitions) =>
        _definitions = definitions;

    public static PortalPermissions Create(IEnumerable<IRoleContributor> contributors)
    {
        var definitions = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in BuiltIns())
            definitions[builtIn.Name] = builtIn;
        foreach (var role in contributors.SelectMany(c => c.Roles))
            definitions[role.Name] = role;
        return new PortalPermissions(definitions);
    }

    /// <summary>
    /// M11 S2 — the same mirror, plus the roles this host has actually SEEDED.
    ///
    /// <para><b>Why this is needed at all.</b> The mirror knows the three built-ins and the roles plugins
    /// contribute. The internal release hands out a role that is neither: <c>AKOS User</c> is an ARM RESOURCE
    /// (<c>Burbuja.Authorization/roleDefinitions/akos-user</c>, written by <c>deploy/seed-roles.sh</c>), so
    /// <see cref="Can"/> answered FALSE for every action of the one key a user is given — which is how a
    /// permission-driven console would have hidden everything from exactly the person it is for.</para>
    ///
    /// <para><b>Where the definitions come from.</b> The kernel's own <c>roleDefinitions</c> collection, which
    /// every principal can read (<c>*/read</c>) and which IS the evaluator's source. A page reads them once per
    /// circuit and asks with the caller's session roles; the names are matched on BOTH the resource name
    /// (<c>akos-user</c>) and the display <c>roleName</c> (<c>AKOS User</c>), because the profile names the
    /// latter and the seeder writes the former.</para>
    /// </summary>
    public PortalPermissions With(IEnumerable<RoleDefinition> seeded)
    {
        var definitions = new Dictionary<string, RoleDefinition>(_definitions, StringComparer.OrdinalIgnoreCase);
        foreach (var role in seeded)
        {
            definitions[role.Name] = role;
            if (role.DisplayName.Length > 0)
                definitions[role.DisplayName] = role;
        }

        return new PortalPermissions(definitions);
    }

    /// <summary>True when any of the caller's roles grants <paramref name="action"/> at all (any scope).</summary>
    public bool CanAny(IReadOnlyList<string> roles, string action) => Can(roles, action);

    /// <summary>The names this mirror can answer for — a fact asserts the seeded set lands here.</summary>
    public IReadOnlyCollection<string> Names => _definitions.Keys.ToArray();

    public IReadOnlyCollection<string> KnownRoles => _definitions.Keys.ToArray();

    /// <summary>True when at least one of the principal's roles grants the action.</summary>
    public bool Can(IReadOnlyList<string> roles, string action) =>
        Can(roles, action, isDataAction: false);

    public bool Can(IReadOnlyList<string> roles, string action, bool isDataAction)
    {
        foreach (var name in roles)
        {
            if (!_definitions.TryGetValue(name, out var role))
                continue;
            var patterns = isDataAction ? role.DataActions : role.Actions;
            var denied = isDataAction ? role.NotDataActions : role.NotActions;
            var granted = patterns.Any(p => ArmActionMatcher.ActionMatches(p, action));
            var refused = denied.Any(p => ArmActionMatcher.ActionMatches(p, action));
            if (granted && !refused)
                return true;
        }

        return false;
    }

    /// <summary>Reader can read; nobody in this list can write.</summary>
    public bool CanWrite(IReadOnlyList<string> roles, string type) =>
        Can(roles, $"{type}/write");

    public bool CanAct(IReadOnlyList<string> roles, string type, string action) =>
        Can(roles, $"{type}/*/action") || Can(roles, $"{type}/{action}/action");

    private static IReadOnlyList<RoleDefinition> BuiltIns() =>
    [
        new("Owner", "Owner", ["*"], [], ["*"], [], ["/"], true),
        new("Contributor", "Contributor", ["*"], ["Burbuja.Authorization/*"], ["*"], ["Burbuja.Authorization/*"], ["/"], true),
        new("Reader", "Reader", ["*/read"], [], ["*/read"], [], ["/"], true),
    ];
}
