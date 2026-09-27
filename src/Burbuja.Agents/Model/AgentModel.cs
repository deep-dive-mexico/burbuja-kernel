using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Context;

namespace Burbuja.Agents.Model;

/// <summary>
/// The `agents` resource. Sync PUT: creating an agent never runs a turn. <see cref="State"/> is
/// derived on GET from the session/inbox facts and is never the source of truth.
/// </summary>
public sealed class AgentProperties
{
    public string DisplayName { get; set; } = "";
    public string? Persona { get; set; }
    public string ModelId { get; set; } = AgentsConstants.DefaultAgentModelId;
    public string Effort { get; set; } = "low";

    /// <summary>
    /// Which registered tools this agent may see. <b>An EMPTY list means EVERY tool</b>, and that meaning is
    /// load-bearing: every ordinary agent relies on it, so it must not be changed to mean "none".
    ///
    /// <para><b>To ask for no tools, name one that is not registered</b> — <c>["none"]</c> is the reserved
    /// sentinel. <c>AgentActivationWorker.ToolSpecs</c> already answers "a filter that names no registered tool
    /// yields no tools, never a silent all tools", so the sentinel needs no special case; it exists because
    /// <c>[]</c> means the opposite of what a reader expects. M11 F1a: the AKOS orchestrator wrote <c>[]</c>
    /// for its personas, so PHYLAX was handed the whole tool catalogue, called <c>list_agents</c>, escalated to
    /// the supervisor and sat parked on an approval for six hours — the corpus gives no persona a tool, and the
    /// one word that would have said so was missing.</para>
    /// </summary>
    public List<string> ToolFilter { get; set; } = [];
    public string? Profile { get; set; }

    /// <summary>
    /// <c>kernel</c> runs model turns in this process; <c>external</c> is driven by another process
    /// over MCP (pomnibot), so an activation only drains its inbox and never calls a model.
    /// </summary>
    public string Driver { get; set; } = AgentDrivers.Kernel;

    public string? ComputeWorkspaceId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? TeamId { get; set; }
    public string? ParentAgentId { get; set; }
    public int Depth { get; set; }
    public List<string> OwnedChildren { get; set; } = [];
    public bool InheritsParentContext { get; set; }
    public string? CurrentSessionId { get; set; }
    public List<InboxItem> Inbox { get; set; } = [];
    public List<string> DeliveredDedupKeys { get; set; } = [];
    public string? PendingApprovalId { get; set; }
    public decimal CostUsd { get; set; }

    // Derived on GET.
    public AgentState State { get; set; } = AgentState.Idle;
    public long? LastSeq { get; set; }
}

public sealed class SessionProperties
{
    public string AgentId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public long LastSeq { get; set; }
    public long EndSeedSeq { get; set; }
    public string? ForkedFromSessionId { get; set; }
    public long? ForkedAtSeq { get; set; }
    public int Turn { get; set; }
    public List<SurfaceOp> SurfaceOps { get; set; } = [];
    public string Status { get; set; } = "active";
    public decimal CostUsd { get; set; }
    public string? TeamId { get; set; }
    public string? ParentSessionId { get; set; }
}

/// <summary>The `agents/contexts` child resource: one bounded, redacted card.</summary>
public sealed class ContextCardProperties
{
    public string Label { get; set; } = "";
    public ContextCardKind Kind { get; set; } = ContextCardKind.Note;
    public int Version { get; set; } = 1;
    public int Bytes { get; set; }
    public string Body { get; set; } = "";
    public string? SourceAgentId { get; set; }
    public string? SourceSessionId { get; set; }
    public long? SourceSeq { get; set; }
    public ContextVisibility Visibility { get; set; } = ContextVisibility.Parent;
}
