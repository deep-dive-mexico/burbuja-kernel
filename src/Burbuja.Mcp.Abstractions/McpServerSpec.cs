namespace Burbuja.Mcp.Abstractions;

/// <summary>
/// How a session reaches an MCP server. Lifted verbatim from pomnibot's engine seam (M5.2) so the
/// consumer re-exports one shape instead of owning a second, drifting copy: the consumer's
/// <c>AgentSessionSpec.McpServers</c> and the kernel's own configuration describe servers the same
/// way, and neither the kernel nor the contract assembly needs the MCP SDK to do it.
/// </summary>
public abstract record McpServerSpec(string Name)
{
    /// <summary>A child process spoken to over stdio.</summary>
    public sealed record Stdio(
        string Name,
        string Command,
        IReadOnlyList<string>? Args = null,
        IReadOnlyDictionary<string, string>? Env = null) : McpServerSpec(Name);

    /// <summary>A Streamable HTTP endpoint (this is what <c>…/mcp</c> is).</summary>
    public sealed record Http(
        string Name,
        string Url,
        IReadOnlyDictionary<string, string>? Headers = null) : McpServerSpec(Name);

    /// <summary>An in-process server bridged over the CLI's <c>mcp_message</c> control request.</summary>
    public sealed record InProcess(string Name) : McpServerSpec(Name);
}

/// <summary>The wire facts both sides of the kernel MCP lane must agree on.</summary>
public static class McpConstants
{
    /// <summary>Where the kernel mounts the Streamable HTTP endpoint.</summary>
    public const string Path = "/mcp";

    /// <summary>The header naming the <c>Burbuja.Agents/agents/{a}</c> the caller acts as.</summary>
    public const string AgentHeader = "x-burbuja-agent";

    /// <summary>
    /// The data action a caller must hold on the agent's scope to run any kernel tool. Read is not
    /// enough: an MCP caller executes tools, so it must be authorised to.
    /// </summary>
    public const string ToolsAction = "Burbuja.Agents/agents/tools/action";

    /// <summary>The stable error code a gated tool call returns when a human has not decided yet.</summary>
    public const string ApprovalPendingCode = "approval_pending";

    /// <summary>Default role a deployment should give the MCP-lane key.</summary>
    public const string LaneRole = "MCP Tool Caller";

    /// <summary>The name of the kernel MCP server as consumers configure it.</summary>
    public const string ServerName = "burbuja";
}
