using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Plugin;
using Burbuja.Plugins.HostFactsMacOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

/// <summary>
/// The manifest is a second copy of what the code says, and the kernel boots on the copy on disk.
/// These facts stage the shipped <c>plugin.json</c> beside the built assembly and boot the real
/// <see cref="PluginHost"/> over it, so "the capability strings are legal" is proven by the schema
/// and the code/disk cross-check rather than by reading them twice.
/// </summary>
public sealed class PluginManifestTests
{
    [Fact]
    public void The_code_manifest_is_what_the_loader_expects()
    {
        var manifest = new HostFactsMacOsPlugin().Manifest;

        manifest.Id.ShouldBe("HostFacts.MacOs");
        manifest.Name.ShouldBe("Host facts (macOS)");
        manifest.Version.ShouldBe("1.0.0");
        manifest.ContractsVersion.ShouldBe(PluginContracts.Version);
        manifest.Assembly.ShouldBe("Burbuja.Plugins.HostFacts.MacOs.dll");
        manifest.EntryType.ShouldBe(typeof(HostFactsMacOsPlugin).FullName);
        manifest.Load.ShouldBe(PluginLoadKind.Dynamic);
        manifest.Provides.ShouldBe(["host.facts"]);
        manifest.ExtensionPoints.ShouldBeEmpty();
        manifest.Requires.ShouldBe([PluginCapability.ProcessExecution, PluginCapability.BackgroundService]);
        manifest.Roles.ShouldBe(["all", "control-plane"]);
        manifest.ConfigSection.ShouldBeNull();
        manifest.Trusted.ShouldBe(true);
        manifest.DependsOn.ShouldBeEmpty();
        manifest.LoadOrder.ShouldBe(340);
    }

    [Fact]
    public void The_shipped_plugin_json_boots_and_registers_the_provider()
    {
        var root = Stage();

        try
        {
            var provider = Boot(root, trusted: "HostFacts.MacOs");

            var facts = provider.GetRequiredService<IHostFactsProvider>();
            facts.Platform.ShouldBe("macos");

            // The plugin really is loaded dynamically: its own type comes from the plugin's load
            // context (same full name, a different Type), while the contract it implements is the
            // unified one — that unification is the whole reason the kernel can resolve it at all.
            facts.GetType().FullName.ShouldBe(typeof(MacOsHostFactsProvider).FullName);
            facts.GetType().ShouldNotBeSameAs(typeof(MacOsHostFactsProvider));
            facts.GetType().GetInterfaces().Single(i => i.FullName == typeof(IHostFactsProvider).FullName)
                .Assembly.ShouldBeSameAs(typeof(IHostFactsProvider).Assembly);
            provider.GetRequiredService<IHostFactsSnapshot>().ShouldBeOfType<Burbuja.Core.Host.HostFactsSnapshot>();
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void BackgroundService_is_why_this_plugin_must_be_trusted()
    {
        // PluginHost refuses a plugin that requires BackgroundService unless the manifest is trusted
        // AND the kernel lists it in Kernel:TrustedPlugins — so staging this plugin without adding
        // `HostFacts.MacOs` there is a boot failure (exit 78), not a silently missing provider.
        var root = Stage();

        try
        {
            var ex = Should.Throw<PluginBootException>(() => Boot(root, trusted: "Policy.Core"));

            ex.Errors.ShouldContain(e => e.Contains("HostFacts.MacOs", StringComparison.Ordinal) && e.Contains("TrustedPlugins", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static ServiceProvider Boot(string pluginsRoot, string trusted)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = pluginsRoot,
            ["Kernel:EnabledPlugins"] = "",
            ["Kernel:TrustedPlugins:0"] = trusted,
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config, []);
        return services.BuildServiceProvider();
    }

    /// <summary>One plugin folder: the built assembly exactly as it ships, plus the committed
    /// plugin.json (copied into this project's output by the csproj, not re-typed here).</summary>
    private static string Stage()
    {
        var root = Path.Combine(Path.GetTempPath(), "burbuja-hostfacts-stage-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "HostFacts.MacOs");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(HostFactsMacOsPlugin).Assembly.Location, Path.Combine(dir, "Burbuja.Plugins.HostFacts.MacOs.dll"), true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "staged-manifest", "plugin.json"), Path.Combine(dir, "plugin.json"), true);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // The plugin load context is non-collectible, so the mapped dll can still be busy. The
            // temp directory is disposable either way.
        }
    }
}
