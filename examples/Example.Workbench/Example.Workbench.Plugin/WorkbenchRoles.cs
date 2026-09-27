using Burbuja.Core.Abstractions.Arm;

namespace Example.Workbench.Plugin;

public sealed class WorkbenchRoles : IRoleContributor
{
    public const string Read = WorkbenchPlugin.Namespace + "/notes/read";
    public const string Write = WorkbenchPlugin.Namespace + "/notes/write";
    public const string Delete = WorkbenchPlugin.Namespace + "/notes/delete";

    // /notes/{name}/events uses the same read action string, but the DATA action bucket.
    public IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new("Workbench Reader", "Read notes", [Read], [], [], [], ["/"], false),
        new("Workbench Editor", "Edit notes and read their trace", [Read, Write, Delete], [], [Read], [], ["/"], false),
    ];
}
