using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Plugin;

/// <summary>
/// Extension points are owned by the plugin that defines them, not by the kernel. A plugin
/// declares its points in <see cref="IBurbujaPlugin.DeclareExtensionPoints"/> and contributes
/// implementations with <c>AddExtension</c>; the host counts registrations per point.
/// </summary>
public sealed class ExtensionPointTests
{
    [Fact]
    public void Declared_points_come_from_plugins_not_the_kernel()
    {
        var registry = new ExtensionPointRegistry();
        var plugin = new TestPlugin(
            "Declarer", ["plugin.point"], [],
            declare: r => r.Declare<IClock>("plugin.point", ExtensionCardinality.Optional));
        var host = BootStatic(plugin, registry);

        var names = host.ExtensionPoints.All.Select(p => p.Name).ToList();
        names.ShouldContain("plugin.point");
        names.ShouldContain("policy.rules");

        // Without the declaring plugin the point does not exist at all — the kernel declares
        // only its own policy.rules point.
        var bare = new ExtensionPointRegistry();
        BootStatic(new PlainPlugin(), bare);
        bare.All.Select(p => p.Name).ShouldNotContain("plugin.point");
    }

    [Fact]
    public void Unknown_extension_point_in_a_manifest_fails_boot()
    {
        var plugin = new TestPlugin("Unknown", ["does.not.exist"], []);
        var ex = Should.Throw<PluginBootException>(() => BootStatic(plugin));
        ex.Errors.ShouldContain(e => e.Contains("unknown extension_points", StringComparison.Ordinal));
    }

    [Fact]
    public void Point_cardinality_is_counted_per_point_not_per_contract()
    {
        // Two ExactlyOne points share one contract, each with exactly one registration. Counting
        // by contract would see two IClock registrations and fail both; counting per point passes.
        var registry = new ExtensionPointRegistry();
        registry.Declare<IClock>("test.clock.a", ExtensionCardinality.ExactlyOne);
        registry.Declare<IClock>("test.clock.b", ExtensionCardinality.ExactlyOne);
        var plugin = new TestPlugin(
            "PerPoint", ["test.clock.a", "test.clock.b"], [],
            configure: s =>
            {
                s.AddExtension<IClock, TestClock>("test.clock.a");
                s.AddExtension<IClock, OtherClock>("test.clock.b");
            });

        var host = BootStatic(plugin, registry);
        host.Loaded.ShouldHaveSingleItem();
    }

    [Fact]
    public void ExactlyOne_point_with_two_registrations_fails_boot()
    {
        var registry = new ExtensionPointRegistry();
        registry.Declare<IClock>("test.clock.one", ExtensionCardinality.ExactlyOne);
        var plugin = new TestPlugin(
            "Two", ["test.clock.one"], [],
            configure: s =>
            {
                s.AddExtension<IClock, TestClock>("test.clock.one");
                s.AddExtension<IClock, OtherClock>("test.clock.one");
            });

        var ex = Should.Throw<PluginBootException>(() => BootStatic(plugin, registry));
        ex.Errors.ShouldContain(e => e.Contains("test.clock.one", StringComparison.Ordinal)
                                     && e.Contains("count=2", StringComparison.Ordinal));
    }

    [Fact]
    public void Optional_point_with_zero_registrations_boots()
    {
        var registry = new ExtensionPointRegistry();
        registry.Declare<IClock>("test.clock.opt", ExtensionCardinality.Optional);
        var plugin = new TestPlugin("Optional", ["test.clock.opt"], []);

        var host = BootStatic(plugin, registry);
        host.Loaded.ShouldHaveSingleItem();
    }

    [Fact]
    public void Point_required_capabilities_must_be_declared_by_the_plugin()
    {
        var registry = new ExtensionPointRegistry();
        registry.Declare<IClock>("test.clock.caps", ExtensionCardinality.Optional, PluginCapability.ProcessExecution);
        var plugin = new TestPlugin("Caps", ["test.clock.caps"], []);

        var ex = Should.Throw<PluginBootException>(() => BootStatic(plugin, registry));
        ex.Errors.ShouldContain(e => e.Contains("requires capabilities", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_without_DeclareExtensionPoints_still_boots()
    {
        var host = BootStatic(new PlainPlugin());
        host.Loaded.ShouldHaveSingleItem().Manifest.Id.ShouldBe("Plain");
    }

    [Fact]
    public void plugin_json_matches_the_code_manifest_for_every_shipped_plugin()
    {
        var pluginRoot = Path.Combine(RepoRoot(), "plugins");
        var dirs = Directory.GetDirectories(pluginRoot).OrderBy(d => d, StringComparer.Ordinal).ToList();
        dirs.ShouldNotBeEmpty();

        foreach (var dir in dirs)
        {
            var jsonPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(jsonPath))
                continue;

            var (disk, errors) = PluginManifestReader.Read(jsonPath);
            errors.ShouldBeEmpty();
            disk.ShouldNotBeNull();

            var dll = FindBuiltPluginDll(dir, disk!.Assembly);
            var alc = new PluginLoadContext(dll);
            var assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
            var type = assembly.GetType(disk.EntryType);
            type.ShouldNotBeNull($"{disk.EntryType} not found in {dll}");
            var plugin = (IBurbujaPlugin)Activator.CreateInstance(type!)!;

            PluginHost.ManifestDiff(disk, plugin.Manifest).ShouldBeEmpty(
                $"installed {disk.Id} must pass the same full manifest check as production boot");
        }
    }

    private static PluginHost BootStatic(IBurbujaPlugin plugin, IExtensionPointRegistry? registry = null)
    {
        var config = new ConfigurationBuilder().Build();
        var kernel = Microsoft.Extensions.Options.Options.Create(new KernelOptions
        {
            Role = "control-plane",
            PluginsDirectory = TempRoot(),
            ContractsVersion = PluginContracts.Version,
        });
        var host = new PluginHost(config, kernel, NullLogger<PluginHost>.Instance, registry);
        host.Boot(new ServiceCollection(), [plugin]);
        return host;
    }

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-ext-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Burbuja.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException($"Burbuja.slnx not found above {AppContext.BaseDirectory}");
        return dir.FullName;
    }

    private static string FindBuiltPluginDll(string pluginDir, string assemblyFile)
    {
        var candidates = Directory.GetFiles(pluginDir, assemblyFile, SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"plugin assembly {assemblyFile} is not built under {pluginDir}");
        return candidates[0];
    }

    private sealed class PlainPlugin : IBurbujaPlugin
    {
        public PluginManifest Manifest { get; } = new(
            "Plain", "Plain", "1.0.0", PluginContracts.Version, "Plain.dll", "X.Y",
            PluginLoadKind.Static, [], [], [], ["all"], null, false, [], 1);

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }

    private sealed class TestPlugin(
        string id,
        IReadOnlyList<string> extensionPoints,
        IReadOnlyList<PluginCapability> requires,
        Action<IExtensionPointRegistry>? declare = null,
        Action<IServiceCollection>? configure = null) : IBurbujaPlugin
    {
        public PluginManifest Manifest { get; } = new(
            id, id, "1.0.0", PluginContracts.Version, id + ".dll", "X.Y",
            PluginLoadKind.Static, [], extensionPoints, requires, ["all"], null, false, [], 1);

        public void ConfigureServices(IServiceCollection services) => configure?.Invoke(services);

        public void DeclareExtensionPoints(IExtensionPointRegistry registry) => declare?.Invoke(registry);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class OtherClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
