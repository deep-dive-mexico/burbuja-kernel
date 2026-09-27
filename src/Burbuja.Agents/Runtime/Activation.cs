using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// Fenced admission and delivery for stateful agents. One activation per agent is admitted by
/// holding the fenced lease <c>agent/{key}</c>; inbox items are delivered according to the
/// nine-row <see cref="DeliveryPlan"/> table.
/// </summary>
public sealed class AgentActivationManager(ILeaseStore leases, IOutbox outbox, IEventLog events, IClock clock)
{
    public const int MaxAgentDepth = AgentAuthority.MaxAgentDepth;
    public const int MaxOwnedChildren = AgentAuthority.MaxOwnedChildren;
    public const int MaxInboxItems = 64;

    /// <summary>Try to become the active activation for an agent. Null means someone else holds it.</summary>
    public async ValueTask<LeaseAcquisition?> AdmitAsync(string agentKey, string owner, TimeSpan ttl, CancellationToken ct) =>
        await leases.AcquireAsync($"agent/{agentKey.ToLowerInvariant()}", owner, ttl, ct);

    public ValueTask ReleaseAsync(string agentKey, string owner, string fencingToken, CancellationToken ct) =>
        leases.ReleaseAsync($"agent/{agentKey.ToLowerInvariant()}", owner, fencingToken, ct);

    /// <summary>Spawns are refused beyond the depth/children budget (fail loud, never silently).</summary>
    public static bool CanSpawn(int parentDepth, int ownedChildren) =>
        parentDepth + 1 <= MaxAgentDepth && ownedChildren < MaxOwnedChildren;

    /// <summary>
    /// Appends an item to the durable inbox and decides what the runtime should do next. A duplicate
    /// dedup key (held by the target) is dropped, never re-delivered.
    /// </summary>
    public InboxDelivery Deliver(
        AgentProperties agent,
        string streamId,
        SendMode mode,
        string text,
        string? dedupKey,
        InboxSourceKind source = InboxSourceKind.HumanSend,
        string? fromAgentId = null,
        SubagentResult? result = null,
        // M14 R12 (request row X1) — additive and last: an image attached to the message, already validated by
        // the door (exactly one of artifact/url, an image media type, and — for an artifact — one that exists).
        AgentImage? image = null,
        // 2026-09-26 (lane CAPS) — the further images of a multi-image message, same validation, additive and last.
        IReadOnlyList<AgentImage>? moreImages = null)
    {
        if (dedupKey is not null && agent.DeliveredDedupKeys.Contains(dedupKey, StringComparer.Ordinal))
            return new InboxDelivery(false, DeliveryAction.InjectOnly, null);

        var item = new InboxItem(
            Guid.NewGuid().ToString("N"),
            source,
            mode,
            text,
            dedupKey,
            clock.UtcNow,
            fromAgentId,
            result,
            image,
            moreImages is { Count: > 0 } ? moreImages : null);
        agent.Inbox.Add(item);
        if (agent.Inbox.Count > MaxInboxItems)
            agent.Inbox.RemoveRange(0, agent.Inbox.Count - MaxInboxItems);
        if (dedupKey is not null)
            agent.DeliveredDedupKeys.Add(dedupKey);

        var action = DeliveryPlan.For(agent.State, mode);
        return new InboxDelivery(true, action, item);
    }

    /// <summary>A settlement notice is produced for every terminal subagent state, and never fails the parent.</summary>
    public InboxItem BuildSettlementNotice(string childAgentId, SubagentResult result) =>
        new(
            Guid.NewGuid().ToString("N"),
            InboxSourceKind.SubagentSettled,
            SendMode.Followup,
            $"subagent {childAgentId} settled: {result.StopReason}",
            "settled:" + childAgentId,
            clock.UtcNow,
            childAgentId,
            result);

    public async ValueTask EnqueueActivationAsync(string agentKey, string op, CancellationToken ct) =>
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicActivation, new JsonObject
        {
            ["agentKey"] = agentKey.ToLowerInvariant(),
            ["op"] = op,
        }), ct);

    internal ValueTask<long> EmitAsync(string streamId, string type, string summary, JsonNode? payload, CancellationToken ct) =>
        events.AppendAsync(new EventAppend(
            streamId,
            null,
            type,
            summary,
            payload,
            RenderLane.Output | RenderLane.Log,
            null,
            null,
            AgentsConstants.SessionStreamType), ct);
}

public sealed record InboxDelivery(bool Accepted, DeliveryAction Action, InboxItem? Item);
