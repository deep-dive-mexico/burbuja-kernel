using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster.Controller;

/// <summary>
/// Chooses a node for one workload.
///
/// <para><b>The kernel owns eligibility; strategies only rank.</b> The rules in <see cref="BuiltInRejection"/>
/// always run and cannot be replaced, so a scorer can express a preference but can never put work on a node
/// that is draining, lacks a requirement or has no room. Contributed filters can only remove more nodes.</para>
///
/// <para><b>Deterministic.</b> The same state yields the same decision: nodes are visited in id order and ties
/// go to the lower id. Nothing here reads measured load to break a tie, because a decision that depends on the
/// load at one instant cannot be explained afterwards.</para>
/// </summary>
internal sealed class Scheduler(IEnumerable<IPlacementFilter> filters, IEnumerable<IPlacementScorer> scorers)
{
    private readonly IPlacementFilter[] _filters = [.. filters];
    private readonly IPlacementScorer[] _scorers = [.. scorers];

    public PlacementDecision Place(WorkloadPlacementRequest request, IReadOnlyList<NodeView> nodes)
    {
        var rejections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? best = null;
        var bestScore = decimal.MinValue;
        foreach (var node in nodes.OrderBy(n => n.Descriptor.NodeId, StringComparer.Ordinal))
        {
            var reason = Rejection(request, node);
            if (reason is not null)
            {
                rejections[node.Descriptor.NodeId] = reason;
                continue;
            }

            var score = BuiltInScore(request, node) + _scorers.Sum(s => s.Score(request, node));
            if (score > bestScore)
            {
                best = node.Descriptor.NodeId;
                bestScore = score;
            }
        }

        return new PlacementDecision(best, rejections);
    }

    public string? Rejection(WorkloadPlacementRequest request, NodeView node) =>
        BuiltInRejection(request, node)
        ?? _filters.Select(f => f.Reject(request, node) is { } reason ? $"{f.Id}: {reason}" : null).FirstOrDefault(r => r is not null);

    /// <summary>
    /// Whether a workload already on <paramref name="node"/> may stay there under a changed spec. Only what
    /// the spec asks for is checked. The node's state of the moment (cordoned, silent, pressed) decides where
    /// new work goes, not whether existing work is uprooted.
    /// </summary>
    public string? RejectionForUpdate(WorkloadPlacementRequest request, NodeView node) =>
        BuiltInRejection(request, node, placed: true)
        ?? _filters.Select(f => f.Reject(request, node) is { } reason ? $"{f.Id}: {reason}" : null).FirstOrDefault(r => r is not null);

    internal static string? BuiltInRejection(WorkloadPlacementRequest request, NodeView node, bool placed = false)
    {
        var spec = request.Spec;
        var descriptor = node.Descriptor;
        if (!placed)
        {
            if (node.Availability != NodeAvailability.Active) return $"node is {node.Availability.ToString().ToLowerInvariant()}";
            if (!node.Reachable) return "node is unreachable";
        }

        if (spec.Placement.Nodes.Count > 0 && !spec.Placement.Nodes.Contains(descriptor.NodeId, StringComparer.Ordinal))
            return "not in the workload's node list";
        if (!descriptor.Kinds.Contains(spec.Kind, StringComparer.Ordinal)) return $"does not run kind '{spec.Kind}'";
        if (!placed && request.RefusedBy.Contains(descriptor.NodeId)) return "the node's own policy refused this workload";

        foreach (var (label, value) in spec.Placement.RequiredLabels)
        {
            if (!descriptor.Labels.TryGetValue(label, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
                return $"label {label}={value} required";
        }

        foreach (var capability in request.RequiredCapabilities)
        {
            if (!descriptor.Capabilities.Contains(capability, StringComparer.Ordinal))
                return $"capability '{capability}' required";
        }

        // The class sets the failure direction. Work nobody is waiting for is refused on a node the cluster
        // cannot see or that is struggling; work somebody is waiting for is still accepted there and only
        // ranked lower, so a dead monitor cannot take the service down with it.
        if (!placed && spec.Class is WorkloadClass.Batch or WorkloadClass.BestEffort)
        {
            if (!node.ObservationFresh) return "no fresh reading from the node; batch work is not started blind";
            if (node.Pressured) return "node is under pressure";
        }

        return ResourceMath.FirstShortfall(spec.Requests, node.Free);
    }

    internal static decimal BuiltInScore(WorkloadPlacementRequest request, NodeView node)
    {
        var spec = request.Spec;
        decimal score = 0;

        // An explicit order outranks everything else that is soft: each step is worth more than the other
        // terms can add up to.
        var preferred = spec.Placement.PreferredNodes;
        for (var i = 0; i < preferred.Count; i++)
        {
            if (!string.Equals(preferred[i], node.Descriptor.NodeId, StringComparison.Ordinal)) continue;
            score += 10_000m * (preferred.Count - i);
            break;
        }

        if (node.Pressured || !node.ObservationFresh) score -= 1_000m;
        if (spec.Placement.SpreadGroup is { Length: > 0 } group && node.SpreadGroups.Contains(group, StringComparer.Ordinal)) score -= 100m;

        // Prefer the node that keeps the largest share of what was asked for free afterwards.
        decimal share = 0;
        var counted = 0;
        foreach (var (name, amount) in spec.Requests)
        {
            if (amount <= 0 || !node.Descriptor.Capacity.TryGetValue(name, out var capacity) || capacity <= 0) continue;
            share += (node.Free.GetValueOrDefault(name) - amount) / capacity;
            counted++;
        }

        return score + (counted == 0 ? 0 : 10m * share / counted);
    }
}
