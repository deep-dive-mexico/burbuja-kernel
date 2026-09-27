namespace Burbuja.Agents.Abstractions;

public static class AgentsConstants
{
    public const string Namespace = "Burbuja.Agents";
    public const string ApiVersion = "2026-09-01";
    public const string TopicRun = "agents.run";

    /// <summary>
    /// W1: the workspace lifecycle lane. A workspace PUT that asks for provisioning answers 202 and enqueues
    /// here; the initializer binds the workspace's defaults and closes the operation. A topic rather than
    /// inline work because provisioning talks to another resource provider through ARM, and a front door that
    /// waited for that would hold the request open for as long as the other RP takes.
    /// </summary>
    public const string TopicWorkspaceLifecycle = "agents.workspace.lifecycle";
    public const string StreamType = "agents.job";
    public const string ScenarioTag = "burbuja.io/scenario";
    public const string DefaultModelId = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";

    /// <summary>Stateful agents default to the DeepSeek workhorse (M4.3/M4.4).</summary>
    public const string DefaultAgentModelId = "deepseek-flash";

    public const string TopicActivation = "agents.activation";
    public const string SessionStreamType = "agents.session";

    public static string StreamId(string canonicalId) =>
        "agents.job/" + canonicalId.ToLowerInvariant();

    public static string SessionStreamId(string canonicalId) =>
        "agents.session/" + canonicalId.ToLowerInvariant();

    public static string LeaseId(string key) => "agents/" + key.ToLowerInvariant();
}

/// <summary>
/// Actions the Agents namespace needs in the RBAC catalogue without an HTTP route behind them.
/// <c>agents/tools/action</c> is what an MCP caller must hold: running a kernel tool through
/// <c>/mcp</c> is not a read, and it is not the ARM action route either.
/// </summary>
public static class AgentActions
{
    public const string Tools = "Burbuja.Agents/agents/tools/action";
}

/// <summary>Who runs an agent's model turns (M5.1). The wire values are part of the resource.</summary>
public static class AgentDrivers
{
    /// <summary>The kernel's activation worker runs the turns.</summary>
    public const string Kernel = "kernel";

    /// <summary>Another process runs the turns and speaks to the kernel over MCP.</summary>
    public const string External = "external";
}
