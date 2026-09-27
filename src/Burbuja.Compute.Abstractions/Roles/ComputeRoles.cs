using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Compute.Abstractions;

public sealed class ComputeRoles : IRoleContributor
{
    public IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(
            "Compute Reader",
            "Compute Reader",
            ["Burbuja.Compute/*/read"],
            [],
            ["Burbuja.Compute/*/read"],
            [],
            ["/"],
            false),
        new(
            "Compute Contributor",
            "Compute Contributor",
            // No delete and no exec: those are Operator-only.
            ["Burbuja.Compute/workspaces/write", "Burbuja.Compute/workspaces/read", "Burbuja.Compute/workspaces/start/action", "Burbuja.Compute/workspaces/stop/action"],
            [],
            ["Burbuja.Compute/workspaces/read"],
            [],
            ["/"],
            false),
        new(
            "Compute Operator",
            "Compute Operator",
            ["Burbuja.Compute/*"],
            [],
            ["Burbuja.Compute/*"],
            [],
            ["/"],
            false),
    ];
}
