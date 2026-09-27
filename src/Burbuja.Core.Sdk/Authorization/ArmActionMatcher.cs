namespace Burbuja.Core.Sdk.Authorization;

/// <summary>ARM action/scope matching shared by the kernel evaluator and the portal UI.</summary>
public static class ArmActionMatcher
{
    public static bool ActionMatches(string pattern, string action)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(action))
            return false;
        if (pattern is "*" or "*/*")
            return true;
        if (string.Equals(pattern, action, StringComparison.OrdinalIgnoreCase))
            return true;
        return SegmentMatch(pattern.Split('/'), 0, action.Split('/'), 0);
    }

    public static bool AssignmentCoversRequest(string assignmentScope, string requestScope)
    {
        var assigned = NormalizeScope(assignmentScope);
        var request = NormalizeScope(requestScope);
        if (assigned is "/" or "")
            return true;
        if (request.Equals(assigned, StringComparison.OrdinalIgnoreCase))
            return true;
        return request.StartsWith(assigned + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInCatalogue(string pattern, IReadOnlyList<string> catalogue)
    {
        if (pattern is "*" or "*/*")
            return true;
        if (pattern.Contains('*', StringComparison.Ordinal))
            return catalogue.Any(action => ActionMatches(pattern, action));
        return catalogue.Any(action => string.Equals(action, pattern, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeScope(string scope)
    {
        if (string.IsNullOrEmpty(scope) || scope == "/")
            return "/";
        return scope.TrimEnd('/');
    }

    public static string ExtensionParentScope(string canonicalResourceId, string extensionNamespace, string extensionType)
    {
        var marker = $"/providers/{extensionNamespace}/{extensionType}/";
        var idx = canonicalResourceId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return canonicalResourceId.Length == 0 ? "/" : canonicalResourceId;
        return idx == 0 ? "/" : canonicalResourceId[..idx];
    }

    private static bool SegmentMatch(string[] pattern, int pi, string[] action, int ai)
    {
        while (pi < pattern.Length)
        {
            if (pattern[pi] == "*")
            {
                if (pi == pattern.Length - 1)
                    return true;
                for (var k = ai; k <= action.Length; k++)
                {
                    if (SegmentMatch(pattern, pi + 1, action, k))
                        return true;
                }

                return false;
            }

            if (ai >= action.Length)
                return false;
            if (!string.Equals(pattern[pi], action[ai], StringComparison.OrdinalIgnoreCase))
                return false;
            pi++;
            ai++;
        }

        return ai == action.Length;
    }
}
