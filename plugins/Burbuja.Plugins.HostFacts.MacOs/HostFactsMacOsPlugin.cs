using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.HostFactsMacOs;

/// <summary>
/// M7.1d: the macOS half of host facts. The plugin owns the platform knowledge (which commands, which
/// output shapes) and nothing else — it does not collect on a timer, does not publish, and does not
/// notify. Whoever consumes <see cref="IHostFactsProvider"/> decides when to collect and what to do
/// with the answer, which is why this manifest declares the <c>BackgroundService</c> capability it
/// needs to be allowed to host such a loop later, without registering a hosted service today.
/// </summary>
public sealed class HostFactsMacOsPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "HostFacts.MacOs",
        "Host facts (macOS)",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.HostFacts.MacOs.dll",
        typeof(HostFactsMacOsPlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["host.facts"],
        [],
        [PluginCapability.ProcessExecution, PluginCapability.BackgroundService],
        ["all", "control-plane"],
        null,
        true,
        [],
        340);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IHostFactsProvider, MacOsHostFactsProvider>();
    }
}
