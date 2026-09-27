using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Deployments.Abstractions;

public sealed class DeploymentsRoles : IRoleContributor
{
    public IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(
            "Deployments Reader",
            "Deployments Reader",
            ["Burbuja.Deployments/*/read"],
            [],
            ["Burbuja.Deployments/*/read"],
            [],
            ["/"],
            false),
        new(
            "Deployments Contributor",
            "Deployments Contributor",
            ["Burbuja.Deployments/*"],
            ["Burbuja.Deployments/environments/delete"],
            ["Burbuja.Deployments/*"],
            [],
            ["/"],
            false),
        new(
            "Deployments Approver",
            "Deployments Approver",
            ["Burbuja.Deployments/*/read", "Burbuja.Resources/approvals/*/action"],
            [],
            ["Burbuja.Deployments/*/read"],
            [],
            ["/"],
            false),
        new(
            "Deployments Operator",
            "Deployments Operator",
            ["Burbuja.Deployments/*"],
            [],
            ["Burbuja.Deployments/*"],
            [],
            ["/"],
            false),
    ];
}
