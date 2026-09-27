using System.Runtime.InteropServices;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Plugins.Test.FakeDep;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Test.ThirdParty;

public sealed class ThirdPartyPlugin : IBurbujaPlugin
{
    // Force a reference to the fake Microsoft.Extensions.* assembly so the plugin cannot
    // load without PluginLoadContext's shared-prefix fallback.
    public static readonly string Probe = Zzz.Hello();

    [DllImport("pomnibot_test")]
    private static extern int pomnibot_test_add(int a, int b);

    public static int Add(int a, int b) => pomnibot_test_add(a, b);

    public PluginManifest Manifest { get; } = new(
        "Test.ThirdParty",
        "Test ThirdParty",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Test.ThirdParty.dll",
        typeof(ThirdPartyPlugin).FullName!,
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
        _ = Probe;
    }
}
