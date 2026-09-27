using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// Builds the kernel-supplied <c>PolicyContext.Peers</c> facts for a running agent, so the
/// adjacency classifiers (<c>AgentReadClassifier</c>/<c>AgentWriteClassifier</c>) have something
/// real to judge instead of always escalating.
///
/// Facts are <c>kind:name</c> with the **bare** agent name, because that is what the tools accept
/// from the model (<c>AgentToolPaths.Resolve</c> turns a bare name into an ARM id). Children come
/// from the children's own <c>parentAgentId</c> — the authoritative record — because
/// <c>ownedChildren</c> is a declared but unmaintained cache and trusting it would silently shrink
/// the adjacency set. No agent and no team means <c>null</c>: "no facts", which the classifiers
/// treat as escalate, never allow.
/// </summary>
public static class AgentAdjacency
{
    public const int MaxAgentsScanned = 512;

    public static async ValueTask<IReadOnlyList<string>?> PeersAsync(
        IResourceStore store,
        string? agentId,
        string? teamId,
        CancellationToken ct)
    {
        var peers = new List<string>();
        var agentKey = agentId?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(agentKey))
        {
            var envelope = await store.GetAsync(agentKey, ct);
            if (envelope?.Properties is JsonObject properties)
            {
                if (Name(properties["parentAgentId"]) is { Length: > 0 } parent)
                    peers.Add("parent:" + parent);

                if (Name(properties["teamId"]) is { Length: > 0 } team)
                    peers.Add("team:" + team);

                foreach (var child in await ChildrenAsync(store, agentKey, ct))
                    peers.Add("child:" + child);
            }
        }

        if (peers.Count == 0 && !string.IsNullOrWhiteSpace(teamId))
            peers.Add("team:" + BareName(teamId));

        return peers.Count == 0 ? null : peers;
    }

    /// <summary>Agents whose <c>parentAgentId</c> points at this agent.</summary>
    private static async ValueTask<List<string>> ChildrenAsync(IResourceStore store, string agentKey, CancellationToken ct)
    {
        var marker = "/providers/" + AgentsConstants.Namespace + "/agents/";
        var idx = agentKey.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return [];

        var prefix = agentKey[..(idx + marker.Length)];
        var page = await store.ListAsync(
            new ResourceStoreQuery(null, null, AgentsConstants.Namespace, "agents", MaxAgentsScanned, null, prefix),
            ct);

        var children = new List<string>();
        foreach (var candidate in page.Value)
        {
            if (string.Equals(candidate.Id, agentKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (candidate.Properties["parentAgentId"]?.GetValue<string>() is not { Length: > 0 } parent)
                continue;
            if (parent.TrimEnd('/').Equals(agentKey, StringComparison.OrdinalIgnoreCase))
                children.Add(BareName(candidate.Id));
        }

        return children;
    }

    private static string? Name(JsonNode? node)
    {
        var value = node?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : BareName(value);
    }

    /// <summary>The last segment of a canonical id; a bare name is returned unchanged.</summary>
    public static string BareName(string value)
    {
        var trimmed = value.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }
}
