using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Example.Workbench.Plugin;

public sealed class WorkbenchPlugin : IBurbujaPlugin
{
    public const string Namespace = "Example.Workbench";

    public PluginManifest Manifest { get; } = new(
        Namespace, "Example Workbench notes", "1.0.0", PluginContracts.Version,
        "Example.Workbench.Plugin.dll", typeof(WorkbenchPlugin).FullName!, PluginLoadKind.Dynamic,
        [Namespace], [], [], ["all"], null, false, [], 900);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IResourceProvider, NotesProvider>();
        services.AddSingleton<IResourceHandler<NoteProperties>, NotesHandler>();
        services.AddSingleton<IEventStreamResolver, NoteEventStreams>();
        services.AddSingleton<IRoleContributor, WorkbenchRoles>();
    }
}
