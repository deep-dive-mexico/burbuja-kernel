using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Model;
using Microsoft.Extensions.Configuration;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// The execution scope a stateful agent's tools run in. Two callers need it — the kernel's
/// activation loop and the kernel MCP server that drives an <c>external</c> agent — and they must
/// agree, or a tool would be allowed in one lane and confined differently in the other.
/// </summary>
public static class AgentToolScope
{
    /// <summary>Configuration key naming the only roots an agent's tools may be pointed at.</summary>
    public const string AllowedRootsKey = "Runtime:AgentAllowedRoots";

    /// <summary>
    /// Where a stateful agent's tools may write.
    ///
    /// <para>The fallback is keyed on the agent's <b>identity</b>, never on its display name: the
    /// display name is caller-set, defaults to empty, and a name that normalises to nothing would
    /// otherwise point every agent at the same directory — or, worse, at the parent of every other
    /// agent's directory. The workspace tools confine strictly to this root, so a shared root is a
    /// shared sandbox.</para>
    ///
    /// <para>A declared <c>workspaceId</c>/<c>computeWorkspaceId</c> is a host path and is only
    /// honoured when it is inside one of <see cref="AllowedRootsKey"/> (or under the temp
    /// directory, which is where the computed fallback lives). Anything else — including the ARM
    /// resource id that is the natural thing to put there today — falls back to the agent's own
    /// directory rather than handing a tool the whole filesystem.</para>
    /// </summary>
    public static string ResolveWorkRoot(AgentProperties agent, string agentKey, IConfiguration configuration)
    {
        foreach (var candidate in new[] { agent.WorkspaceId, agent.ComputeWorkspaceId })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate) && IsAllowed(candidate, configuration))
                return Path.GetFullPath(candidate);
        }

        var fallback = Path.Combine(Path.GetTempPath(), "burbuja-agents", EntityIds.Derive(agentKey).ToString("N")[..16]);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// True when <paramref name="path"/> is inside the temp directory or a configured allowed root.
    /// The filesystem root and the home directory are never allowed, so an agent PUT cannot turn a
    /// tool loose on the machine.
    /// </summary>
    public static bool IsAllowed(string path, IConfiguration configuration)
    {
        var full = Path.GetFullPath(path);
        if (full == Path.GetPathRoot(full))
            return false;

        foreach (var root in AllowedRoots(configuration).Append(Path.GetTempPath()))
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var normalized = Path.GetFullPath(root);
            if (normalized == Path.GetPathRoot(normalized))
                continue;
            if (full.Equals(normalized, StringComparison.Ordinal))
                return true;
            var prefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> AllowedRoots(IConfiguration configuration)
    {
        var configured = configuration.GetSection(AllowedRootsKey).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();

        // The compute roots are legitimate places for an agent to work: that is what a workspace is.
        foreach (var key in new[] { "Compute:LocalDirectory:Root", "Compute:Git:Root" })
        {
            if (configuration[key] is { Length: > 0 } root)
                configured.Add(root);
        }

        return configured;
    }

    public static TimeSpan ToolTimeout(IConfiguration configuration) =>
        TimeSpan.TryParse(configuration["Runtime:ToolTimeout"], out var tt) ? tt : TimeSpan.FromMinutes(10);

    public static ToolExecutionContext Context(
        AgentProperties agent,
        string agentKey,
        string streamId,
        string runId,
        string jobId,
        IConfiguration configuration) =>
        new(ResolveWorkRoot(agent, agentKey, configuration), streamId, runId, jobId, ToolTimeout(configuration), new Dictionary<string, string>());
}
