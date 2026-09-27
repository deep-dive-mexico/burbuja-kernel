using System.Collections.Concurrent;
using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Burbuja.Core.Sdk.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Arm;

public sealed class AuthorizationEvaluator : IAuthorizationEvaluator
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, AuthorizationResult> _cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IRoleContributor> _contributors;
    private Snapshot _snapshot;

    public IReadOnlyList<string> ActionCatalogue { get; private set; } = [];

    private AuthorizationEvaluator(Snapshot snapshot, IReadOnlyList<IRoleContributor> contributors)
    {
        _snapshot = snapshot;
        _contributors = contributors;
    }

    public static AuthorizationEvaluator Create(IServiceProvider services)
    {
        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var store = services.GetRequiredService<IResourceStore>();
        var contributors = services.GetServices<IRoleContributor>().ToArray();
        var definitions = BuiltIns().ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var role in contributors.SelectMany(c => c.Roles))
            definitions[role.Name] = role;

        var assignments = new List<RoleAssignment>();
        foreach (var key in auth.ApiKeys)
        {
            foreach (var role in key.Roles)
            {
                assignments.Add(new RoleAssignment(
                    // M10 H2: a STABLE name, derived from the pair it grants.
                    //
                    // This was `Guid.NewGuid()`, and `Rehydrate` replaces an assignment by NAME from the store.
                    // A profile-configured role's assignment therefore had a name that changed on every boot
                    // and matched nothing stored — so the grant existed only in the process that created it.
                    // Measured on the release host 2026-09-13: the reader key read fine immediately after an
                    // install and was refused after the next restart, which is the shape of a grant that
                    // silently does not survive a deploy.
                    $"key:{key.PrincipalId}:{role}".ToLowerInvariant(),
                    "/",
                    key.PrincipalId,
                    role,
                    DateTimeOffset.UtcNow,
                    "system",
                    null));
            }
        }

        var evaluator = new AuthorizationEvaluator(new Snapshot(definitions, assignments), contributors);
        evaluator.Rehydrate(store);
        return evaluator;
    }

    public void BindCatalogueAndValidate(IReadOnlyList<string> catalogue)
    {
        ActionCatalogue = catalogue;
        var errors = new List<string>();
        foreach (var role in _contributors.SelectMany(c => c.Roles))
        {
            foreach (var action in role.Actions.Concat(role.NotActions).Concat(role.DataActions).Concat(role.NotDataActions))
            {
                if (!ArmActionMatcher.IsInCatalogue(action, catalogue))
                    errors.Add($"role '{role.Name}' contributed action '{action}' is not in the ARM action catalogue");
            }
        }

        if (errors.Count > 0)
            throw new PluginBootException(errors);
    }

    public void Rehydrate(IResourceStore store)
    {
        var definitions = Current.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var assignments = Current.Assignments.ToList();

        foreach (var env in ListAll(store, $"{ArmConstants.AuthorizationNamespace}/roleDefinitions"))
        {
            var props = env.Properties.Deserialize<Rps.RoleDefinitionProperties>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (props is null)
                continue;
            var name = env.Name;
            definitions[name] = ToDefinition(name, props);
        }

        foreach (var env in ListAll(store, $"{ArmConstants.AuthorizationNamespace}/roleAssignments"))
        {
            var props = env.Properties.Deserialize<Rps.RoleAssignmentProperties>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (props is null)
                continue;
            var scope = ArmActionMatcher.ExtensionParentScope(env.Id, ArmConstants.AuthorizationNamespace, "roleAssignments");
            var roleName = RoleNameFromId(props.RoleDefinitionId);
            assignments.RemoveAll(a => string.Equals(a.Name, env.Name, StringComparison.OrdinalIgnoreCase));
            assignments.Add(new RoleAssignment(
                env.Name,
                scope,
                props.PrincipalId,
                roleName,
                env.SystemData.CreatedAt ?? DateTimeOffset.UtcNow,
                env.SystemData.CreatedBy ?? "system",
                string.IsNullOrWhiteSpace(props.Condition) ? null : props.Condition,
                props.ConditionVersion));
        }

        Swap(new Snapshot(definitions, assignments));
    }

    public void UpsertDefinition(RoleDefinition definition)
    {
        var definitions = Current.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        definitions[definition.Name] = definition;
        Swap(new Snapshot(definitions, Current.Assignments.ToList()));
    }

    public void RemoveDefinition(string name)
    {
        var definitions = Current.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        definitions.Remove(name);
        Swap(new Snapshot(definitions, Current.Assignments.ToList()));
    }

    public void UpsertAssignment(RoleAssignment assignment)
    {
        var assignments = Current.Assignments.Where(a => !string.Equals(a.Name, assignment.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        assignments.Add(assignment);
        Swap(new Snapshot(Current.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase), assignments));
    }

    public void RemoveAssignment(string name)
    {
        var assignments = Current.Assignments.Where(a => !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        Swap(new Snapshot(Current.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase), assignments));
    }

    public bool TryGetDefinition(string name, out RoleDefinition definition) =>
        Current.Definitions.TryGetValue(name, out definition!);

    public ValueTask<AuthorizationResult> AuthorizeAsync(ArmPrincipal principal, string scope, string action, bool isDataAction, CancellationToken ct) =>
        AuthorizeAsync(principal, scope, action, isDataAction, AuthorizationContext.Empty, ct);

    public ValueTask<AuthorizationResult> AuthorizeAsync(
        ArmPrincipal principal,
        string scope,
        string action,
        bool isDataAction,
        AuthorizationContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snap = Current;
        // A conditional assignment depends on request facts, so nothing about this decision may
        // be memoised while any condition exists in the snapshot.
        var cacheable = !snap.HasConditions;
        var cacheKey = $"{principal.PrincipalId}\n{scope}\n{action}\n{isDataAction}";
        if (cacheable && _cache.TryGetValue(cacheKey, out var cached))
            return new(cached);

        var allowed = false;
        foreach (var assignment in snap.Assignments)
        {
            if (!PrincipalMatches(principal, assignment.PrincipalId))
                continue;
            if (!ArmActionMatcher.AssignmentCoversRequest(assignment.Scope, scope))
                continue;
            if (!snap.Definitions.TryGetValue(assignment.RoleDefinitionName, out var role))
                continue;

            var actions = isDataAction ? role.DataActions : role.Actions;
            var notActions = isDataAction ? role.NotDataActions : role.NotActions;
            var granted = actions.Any(p => ArmActionMatcher.ActionMatches(p, action));
            var excluded = notActions.Any(p => ArmActionMatcher.ActionMatches(p, action));
            if (!granted || excluded)
                continue;

            if (!string.IsNullOrWhiteSpace(assignment.Condition))
            {
                var conditionContext = new RoleConditionContext(
                    context.ResourceName,
                    context.ResourceTags,
                    context.RequestTags,
                    principal.PrincipalId,
                    principal.Claims);
                // A malformed condition makes TryEvaluate return false — the assignment is skipped.
                if (!RoleConditionParser.TryEvaluate(assignment.Condition, conditionContext, out var conditionAllows)
                    || !conditionAllows)
                    continue;
            }

            allowed = true;
            break;
        }

        var result = allowed
            ? new AuthorizationResult(true, action, scope, null)
            : new AuthorizationResult(
                false,
                action,
                scope,
                $"The client '{principal.PrincipalId}' with object id '{principal.PrincipalId}' does not have authorization to perform action '{action}' over scope '{scope}'.");
        if (cacheable)
            _cache[cacheKey] = result;
        return new(result);
    }

    internal static string RoleNameFromId(string roleDefinitionId)
    {
        if (string.IsNullOrEmpty(roleDefinitionId))
            return roleDefinitionId;
        var slash = roleDefinitionId.LastIndexOf('/');
        return slash < 0 ? roleDefinitionId : roleDefinitionId[(slash + 1)..];
    }

    public static IReadOnlyList<RoleDefinition> BuiltIns() =>
    [
        new("Owner", "Owner", ["*"], [], ["*"], [], ["/"], true),
        new("Contributor", "Contributor", ["*"], ["Burbuja.Authorization/*"], ["*"], ["Burbuja.Authorization/*"], ["/"], true),
        new("Reader", "Reader", ["*/read"], [], ["*/read"], [], ["/"], true),
    ];

    public static RoleDefinition ToDefinition(string name, Rps.RoleDefinitionProperties props) =>
        new(
            name,
            string.IsNullOrEmpty(props.RoleName) ? name : props.RoleName,
            props.Actions,
            props.NotActions,
            props.DataActions,
            props.NotDataActions,
            props.AssignableScopes.Count == 0 ? ["/"] : props.AssignableScopes,
            false);

    private Snapshot Current
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    private void Swap(Snapshot snapshot)
    {
        lock (_gate)
            _snapshot = snapshot;
        _cache.Clear();
    }

    private static bool PrincipalMatches(ArmPrincipal principal, string assignmentPrincipalId) =>
        string.Equals(principal.PrincipalId, assignmentPrincipalId, StringComparison.Ordinal)
        || principal.Groups.Contains(assignmentPrincipalId, StringComparer.Ordinal);

    private static List<ResourceEnvelope<System.Text.Json.Nodes.JsonObject>> ListAll(IResourceStore store, string type)
    {
        var all = new List<ResourceEnvelope<System.Text.Json.Nodes.JsonObject>>();
        string? token = null;
        do
        {
            var page = store.ListAsync(new ResourceStoreQuery(null, null, ArmConstants.AuthorizationNamespace, type, 1000, token), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            all.AddRange(page.Value);
            token = page.NextLink;
        }
        while (!string.IsNullOrEmpty(token));
        return all;
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, RoleDefinition> Definitions,
        IReadOnlyList<RoleAssignment> Assignments)
    {
        // Conditional assignments depend on request facts, so their decisions must not be memoised.
        public bool HasConditions => Assignments.Any(a => !string.IsNullOrWhiteSpace(a.Condition));
    }
}
