using System.Text.RegularExpressions;

namespace Burbuja.Core.Sdk.Authorization;

public enum RoleConditionField
{
    ResourceName,
    ResourceTag,
    RequestTag,
    PrincipalId,
    PrincipalClaim,
}

public enum RoleConditionOperator
{
    Equals,
    NotEquals,
    StartsWith,
}

public sealed record RoleConditionClause(RoleConditionField Field, string? Key, RoleConditionOperator Operator, string Value);

/// <summary>A parsed role-assignment condition: an <c>AllOf</c> of clauses.</summary>
public sealed record RoleCondition(IReadOnlyList<RoleConditionClause> Clauses);

/// <summary>The request facts a <see cref="RoleCondition"/> is evaluated against.</summary>
public sealed record RoleConditionContext(
    string? ResourceName,
    IReadOnlyDictionary<string, string>? ResourceTags,
    IReadOnlyDictionary<string, string>? RequestTags,
    string? PrincipalId,
    IReadOnlyDictionary<string, string>? PrincipalClaims);

/// <summary>
/// N7: a minimal, ARM-shaped role-assignment condition language. Grammar (whitespace tolerant,
/// keywords case-insensitive):
/// <code>
///   condition := 'AllOf' '(' clause (',' clause)* ')'
///   clause    := '@Resource' '[' 'name' ']' op quoted
///              | '@Resource' '[' 'tags:' key ']' op quoted
///              | '@Request'  '[' 'tags:' key ']' op quoted
///              | '@Principal' '[' 'id' ']' op quoted
///              | '@Principal' '[' 'claims:' key ']' op quoted
///   op        := 'eq' | 'ne' | 'startswith'
///   quoted    := "'" value "'"
/// </code>
/// Anything else fails to parse, and a malformed condition must deny (fail closed).
/// </summary>
public static class RoleConditionParser
{
    private static readonly Regex ClauseRx = new(
        @"^(?<scope>@Resource|@Request|@Principal)\s*\[\s*(?<field>[A-Za-z_]+(?::[A-Za-z0-9_./-]+)?)\s*\]\s*(?<op>eq|ne|startswith)\s*'(?<value>[^']*)'$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string? condition, out RoleCondition? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(condition))
            return true;

        var text = condition.Trim();
        if (!text.StartsWith("AllOf", StringComparison.OrdinalIgnoreCase))
        {
            error = "a condition must be an AllOf(...) expression";
            return false;
        }

        var open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(')'))
        {
            error = "AllOf(...) is missing parentheses";
            return false;
        }

        var inner = text[(open + 1)..^1];
        var clauses = new List<RoleConditionClause>();
        foreach (var raw in SplitTopLevel(inner))
        {
            var clauseText = raw.Trim();
            if (clauseText.Length == 0)
            {
                error = "empty clause";
                return false;
            }

            var m = ClauseRx.Match(clauseText);
            if (!m.Success)
            {
                error = $"unsupported clause '{clauseText}'";
                return false;
            }

            var scope = m.Groups["scope"].Value;
            var field = m.Groups["field"].Value;
            var op = m.Groups["op"].Value.ToLowerInvariant() switch
            {
                "eq" => RoleConditionOperator.Equals,
                "ne" => RoleConditionOperator.NotEquals,
                _ => RoleConditionOperator.StartsWith,
            };
            var value = m.Groups["value"].Value;

            RoleConditionClause clause;
            if (field.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (!scope.Equals("@Resource", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"'name' is only valid on @Resource, not {scope}";
                    return false;
                }

                clause = new RoleConditionClause(RoleConditionField.ResourceName, null, op, value);
            }
            else if (field.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (!scope.Equals("@Principal", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"'id' is only valid on @Principal, not {scope}";
                    return false;
                }

                clause = new RoleConditionClause(RoleConditionField.PrincipalId, null, op, value);
            }
            else if (field.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
            {
                var key = field["tags:".Length..];
                clause = scope.ToLowerInvariant() switch
                {
                    "@resource" => new RoleConditionClause(RoleConditionField.ResourceTag, key, op, value),
                    "@request" => new RoleConditionClause(RoleConditionField.RequestTag, key, op, value),
                    _ => null!,
                };
                if (clause is null)
                {
                    error = $"'tags:' is not valid on {scope}";
                    return false;
                }
            }
            else if (field.StartsWith("claims:", StringComparison.OrdinalIgnoreCase))
            {
                if (!scope.Equals("@Principal", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"'claims:' is only valid on @Principal, not {scope}";
                    return false;
                }

                clause = new RoleConditionClause(RoleConditionField.PrincipalClaim, field["claims:".Length..], op, value);
            }
            else
            {
                error = $"unsupported field '{field}'";
                return false;
            }

            clauses.Add(clause);
        }

        if (clauses.Count == 0)
        {
            error = "AllOf(...) needs at least one clause";
            return false;
        }

        parsed = new RoleCondition(clauses);
        return true;
    }

    public static bool TryEvaluate(string? condition, RoleConditionContext context, out bool allowed)
    {
        allowed = false;
        if (!TryParse(condition, out var parsed, out _))
            return false; // malformed denies

        if (parsed is null)
        {
            allowed = true; // no condition
            return true;
        }

        allowed = parsed.Clauses.All(c => EvaluateClause(c, context));
        return true;
    }

    private static bool EvaluateClause(RoleConditionClause clause, RoleConditionContext context)
    {
        var actual = clause.Field switch
        {
            RoleConditionField.ResourceName => context.ResourceName,
            RoleConditionField.ResourceTag => Lookup(context.ResourceTags, clause.Key),
            RoleConditionField.RequestTag => Lookup(context.RequestTags, clause.Key),
            RoleConditionField.PrincipalId => context.PrincipalId,
            RoleConditionField.PrincipalClaim => Lookup(context.PrincipalClaims, clause.Key),
            _ => null,
        };

        // A condition over a missing attribute is false — never a silent allow.
        if (actual is null)
            return false;

        return clause.Operator switch
        {
            RoleConditionOperator.Equals => string.Equals(actual, clause.Value, StringComparison.OrdinalIgnoreCase),
            RoleConditionOperator.NotEquals => !string.Equals(actual, clause.Value, StringComparison.OrdinalIgnoreCase),
            RoleConditionOperator.StartsWith => actual.StartsWith(clause.Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string? Lookup(IReadOnlyDictionary<string, string>? map, string? key) =>
        map is not null && key is not null && map.TryGetValue(key, out var value) ? value : null;

    private static IEnumerable<string> SplitTopLevel(string inner)
    {
        var depth = 0;
        var start = 0;
        var inQuote = false;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\'')
                inQuote = !inQuote;
            else if (!inQuote && c == '(')
                depth++;
            else if (!inQuote && c == ')')
                depth--;
            else if (!inQuote && depth == 0 && c == ',')
            {
                yield return inner[start..i];
                start = i + 1;
            }
        }

        yield return inner[start..];
    }
}
