using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Test.Ok;

public sealed class OkPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Test.Ok",
        "Test Ok",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Test.Ok.dll",
        typeof(OkPlugin).FullName!,
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
    }
}
