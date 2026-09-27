using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Clock;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Test.CoreRef;

public sealed class CoreRefPlugin : IBurbujaPlugin
{
    // Keep a live kernel type so the assembly manifest references Burbuja.Core.
    public static readonly Type KernelMarker = typeof(TimeProviderClock);
    public PluginManifest Manifest { get; } = new(
        "Test.CoreRef",
        "Test CoreRef",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Test.CoreRef.dll",
        typeof(CoreRefPlugin).FullName!,
        PluginLoadKind.Dynamic,
        [],
        [],
        [],
        ["all"],
        null,
        false,
        [],
        50);

    public void ConfigureServices(IServiceCollection services)
    {
        _ = typeof(TimeProviderClock);
    }
}
