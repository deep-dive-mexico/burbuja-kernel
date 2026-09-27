using Burbuja.Agents.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Mcp.Abstractions;

namespace Burbuja.Mcp;

/// <summary>
/// The role the MCP-lane key is given. It is deliberately small: read an agent's context and call
/// its tools, nothing else. The key that a child process sees is on disk in a worktree's reach, so
/// it must never be the Owner key — decision 4 of the M5 plan is "the caller is authorised, not
/// trusted", and this is the half of it that a deployment configures.
/// </summary>
public sealed class McpRoles : IRoleContributor
{
    public IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(
            McpConstants.LaneRole,
            McpConstants.LaneRole,
            [],
            [],
            ["Burbuja.Agents/*/read", AgentActions.Tools],
            [],
            ["/"],
            false),
    ];
}
