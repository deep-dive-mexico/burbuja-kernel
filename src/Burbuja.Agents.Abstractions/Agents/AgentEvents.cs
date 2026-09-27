namespace Burbuja.Agents.Abstractions.Agents;

/// <summary>
/// The stateful-runtime event catalogue (M4.3). Names are part of the wire: every type is
/// namespaced under <c>agents.</c> and is asserted by a test.
/// </summary>
public static class AgentSessionEvents
{
    public const string Started = "agents.session.started";
    public const string Message = "agents.session.message";
    public const string Compacted = "agents.session.compacted";
    public const string Ended = "agents.session.ended";
    public const string Forked = "agents.session.forked";
    public const string Seed = "agents.session.seed";
}

public static class AgentActivationEvents
{
    public const string Started = "agents.activation.started";
    public const string Settled = "agents.activation.settled";
    public const string Resumed = "agents.activation.resumed";
}

public static class AgentInboxEvents
{
    public const string Enqueued = "agents.inbox.enqueued";
    public const string Delivered = "agents.inbox.delivered";
    public const string Dropped = "agents.inbox.dropped";
}

public static class AgentSubagentEvents
{
    public const string Spawned = "agents.subagent.spawned";
    public const string Settled = "agents.subagent.settled";
}

public static class AgentContextEvents
{
    public const string Posted = "agents.context.posted";
    public const string Read = "agents.context.read";
}

public static class AgentTeamEvents
{
    public const string Created = "agents.team.created";
    public const string MemberAdded = "agents.team.member.added";
    public const string TaskChanged = "agents.team.task.changed";
    public const string MessageQueued = "agents.team.message.queued";
    public const string MessageDelivered = "agents.team.message.delivered";
    public const string Folded = "agents.team.folded";
}

public static class AgentRuntimeEvents
{
    public const string SpawnDenied = "agents.spawn.denied";
    public const string AuthorityDenied = "agents.authority.denied";
    public const string Handoff = "agents.handoff";
}
