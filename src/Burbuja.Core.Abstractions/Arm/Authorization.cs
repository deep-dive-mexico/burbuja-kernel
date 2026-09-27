namespace Burbuja.Core.Abstractions.Arm;

public sealed record ArmPrincipal(
    string PrincipalId,
    string PrincipalType,
    string? TenantId,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, string> Claims);

public sealed record RoleDefinition(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> NotActions,
    IReadOnlyList<string> DataActions,
    IReadOnlyList<string> NotDataActions,
    IReadOnlyList<string> AssignableScopes,
    bool IsBuiltIn);

public sealed record RoleAssignment(
    string Name,
    string Scope,
    string PrincipalId,
    string RoleDefinitionName,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? Condition,
    string? ConditionVersion = null);

public sealed record AuthorizationResult(bool Allowed, string Action, string Scope, string? DeniedBecause);

/// <summary>
/// N7: request facts a role-assignment condition may reference. Resource tags come from the
/// request body, request tags from <c>tags.*</c> query parameters.
/// </summary>
public sealed record AuthorizationContext(
    string? ResourceName = null,
    IReadOnlyDictionary<string, string>? ResourceTags = null,
    IReadOnlyDictionary<string, string>? RequestTags = null)
{
    public static readonly AuthorizationContext Empty = new();
}

public interface IAuthorizationEvaluator
{
    ValueTask<AuthorizationResult> AuthorizeAsync(ArmPrincipal principal, string scope, string action, bool isDataAction, CancellationToken ct);
    IReadOnlyList<string> ActionCatalogue { get; }

    /// <summary>
    /// Condition-aware overload. The default ignores the context and delegates to the
    /// condition-free overload (implementations that support conditions override it).
    /// </summary>
    ValueTask<AuthorizationResult> AuthorizeAsync(
        ArmPrincipal principal,
        string scope,
        string action,
        bool isDataAction,
        AuthorizationContext context,
        CancellationToken ct) =>
        AuthorizeAsync(principal, scope, action, isDataAction, ct);
}

public interface IRoleContributor
{
    IReadOnlyList<RoleDefinition> Roles { get; }
}
