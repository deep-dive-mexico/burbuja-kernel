using System.Text.Json.Nodes;

namespace Burbuja.Agents.Abstractions.Agents;

/// <summary>Derived on GET from session/inbox facts — never stored as the source of truth.</summary>
public enum AgentState
{
    Idle,
    Running,
    Waiting,
    Settled,
}

public enum SendMode
{
    Followup,
    Steer,
    Inject,
}

public enum InboxSourceKind
{
    AgentMessage,
    SubagentSettled,
    HumanSend,
    TeamMessage,
    ContextInject,
}

public enum SubagentStopReason
{
    Completed,
    Failed,
    Cancelled,
    DepthBudgetExceeded,
    UnsupportedModelCapability,
}

public sealed record SubagentResult(
    string Output,
    JsonNode? Structured,
    string? Diagnostic,
    SubagentStopReason StopReason);

/// <summary>
/// M14 R12 (request row X1) — an image a person attached to a message.
///
/// <para><b>Why a reference and not the bytes.</b> The inbox is durable resource state: base64 of a 3 MB photo
/// inside an agent's properties would be copied into every read of that agent, every event that carries it and
/// every backup of the store. So a message carries HOW to get the image — an artifact this kernel stores, or a
/// URL the channel already hosts — and the runtime resolves it once, at the moment it builds the model call.
/// The two forms are exclusive: a message that named both would be a message with two answers to "which image",
/// and the door refuses it rather than picking one.</para>
/// </summary>
public sealed record AgentImage(
    string? ArtifactKey = null,
    string? Url = null,
    string MediaType = "image/png",
    string? Alt = null)
{
    public bool HasArtifact => ArtifactKey is { Length: > 0 };
    public bool HasUrl => Url is { Length: > 0 };
}

public sealed record InboxItem(
    string Id,
    InboxSourceKind Source,
    SendMode Mode,
    string Text,
    string? DedupKey,
    DateTimeOffset EnqueuedAt,
    string? FromAgentId = null,
    SubagentResult? Result = null,
    AgentImage? Image = null,
    /// <summary>
    /// 2026-09-26 (AKOS wave 2, lane CAPS) — the images AFTER the first, for a message that carries several
    /// (ÓPTICA compares two photos, or reads a document photographed in pages). Additive and last so every
    /// positional caller still compiles, and a separate list rather than a change of <see cref="Image"/>'s type
    /// so an inbox persisted before today still deserialises. The door bounds the total (image + these) at
    /// four; the runtime resolves each exactly as it resolves <see cref="Image"/>.
    /// </summary>
    IReadOnlyList<AgentImage>? MoreImages = null);

/// <summary>A compacted range in a session's transcript.</summary>
public sealed record SurfaceOp(
    string Kind,
    string? Label,
    long FromSeq,
    long ToSeq,
    string? Summary);

public sealed record AgentLifecycleView(AgentState State, string? CurrentSessionId, long? LastSeq, int Depth, bool HasPendingApproval);

public static class AgentLifecycleRules
{
    /// <summary>Derive the observed state; the log and inbox remain the source of truth.</summary>
    public static AgentState Derive(bool running, bool waiting, bool settled) =>
        running ? AgentState.Running
        : waiting ? AgentState.Waiting
        : settled ? AgentState.Settled
        : AgentState.Idle;
}

public enum DeliveryAction
{
    /// <summary>Append to the lane and let the running activation pick it up at the next step.</summary>
    Enqueue,
    /// <summary>Append and wake a waiting activation.</summary>
    EnqueueAndWake,
    /// <summary>Cold-resume the session, then append.</summary>
    ColdResumeThenEnqueue,
    /// <summary>Append without waking and without resuming (inject never resumes).</summary>
    InjectOnly,
}

/// <summary>The nine-row delivery table: <see cref="AgentState"/> × <see cref="SendMode"/>.</summary>
public static class DeliveryPlan
{
    public static DeliveryAction For(AgentState state, SendMode mode) => (state, mode) switch
    {
        (AgentState.Running, SendMode.Followup) => DeliveryAction.Enqueue,
        (AgentState.Running, SendMode.Steer) => DeliveryAction.Enqueue,
        (AgentState.Running, SendMode.Inject) => DeliveryAction.InjectOnly,
        (AgentState.Waiting, SendMode.Followup) => DeliveryAction.EnqueueAndWake,
        (AgentState.Waiting, SendMode.Steer) => DeliveryAction.EnqueueAndWake,
        (AgentState.Waiting, SendMode.Inject) => DeliveryAction.InjectOnly,
        (AgentState.Settled, SendMode.Followup) => DeliveryAction.ColdResumeThenEnqueue,
        (AgentState.Settled, SendMode.Steer) => DeliveryAction.ColdResumeThenEnqueue,
        (AgentState.Settled, SendMode.Inject) => DeliveryAction.InjectOnly,
        _ => DeliveryAction.Enqueue,
    };
}

/// <summary>
/// Adjacency rules over the kernel-supplied <c>PolicyContext.Peers</c> facts. A missing or
/// unparseable target is never an allow.
/// </summary>
public static class AgentAuthority
{
    public const int MaxAgentDepth = 3;
    public const int MaxOwnedChildren = 4;

    public static bool MayAddress(IReadOnlyList<string>? peers, string targetAgentId)
    {
        if (peers is null || targetAgentId.Length == 0)
            return false;
        foreach (var peer in peers)
        {
            var idx = peer.IndexOf(':');
            if (idx <= 0)
                continue;
            var kind = peer[..idx];
            var id = peer[(idx + 1)..];
            if (!id.Equals(targetAgentId, StringComparison.Ordinal))
                continue;
            if (kind is "parent" or "child" or "team")
                return true;
        }

        return false;
    }

    /// <summary>Inject is only ever allowed towards a direct child.</summary>
    public static bool MayInject(IReadOnlyList<string>? peers, string targetAgentId)
    {
        if (peers is null || targetAgentId.Length == 0)
            return false;
        foreach (var peer in peers)
        {
            if (peer.StartsWith("child:", StringComparison.Ordinal)
                && peer["child:".Length..].Equals(targetAgentId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
