using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Agents.Roles;

public sealed class AgentsRoles : IRoleContributor
{
    public IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(
            "Agents Reader",
            "Agents Reader",
            ["Burbuja.Agents/*/read"],
            [],
            ["Burbuja.Agents/*/read"],
            [],
            ["/"],
            false),
        new(
            "Job Submitter",
            "Job Submitter",
            [
                "Burbuja.Agents/*/read",
                "Burbuja.Agents/workspaces/jobs/write",
                "Burbuja.Agents/workspaces/jobs/*/action",
            ],
            [],
            ["Burbuja.Agents/*/read"],
            [],
            ["/"],
            false),
        new(
            "Agents Approver",
            "Agents Approver",
            ["Burbuja.Agents/*/read", "Burbuja.Resources/approvals/*/action"],
            [],
            ["Burbuja.Agents/*/read"],
            [],
            ["/"],
            false),
        new(
            "Agents Operator",
            "Agents Operator",
            ["Burbuja.Agents/*"],
            [],
            ["Burbuja.Agents/*"],
            [],
            ["/"],
            false),
    ];
}
