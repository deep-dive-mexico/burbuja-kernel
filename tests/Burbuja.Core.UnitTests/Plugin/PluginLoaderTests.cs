using Burbuja.Core.Clock;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Burbuja.Plugins.Test.CoreRef;
using Burbuja.Plugins.Test.Ok;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Plugin;

public sealed class PluginLoaderTests
{
    [Fact]
    public void Type_identity_unifies_IBurbujaPlugin_to_default_context()
    {
        var root = Stage(typeof(OkPlugin), File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.Ok")));
        var host = Boot(root);
        var loaded = host.Loaded.ShouldHaveSingleItem();
        var iface = loaded.Instance.GetType().GetInterfaces().Single(i => i.FullName == typeof(IBurbujaPlugin).FullName);
        ReferenceEquals(iface.Assembly, typeof(IBurbujaPlugin).Assembly).ShouldBeTrue();
    }

    [Fact]
    public void Invalid_plugin_json_fails_schema_validation()
    {
        var root = TempRoot();
        var dir = Path.Combine(root, "Bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"id":1}""");
        var ex = Should.Throw<PluginBootException>(() => Boot(root));
        ex.Errors.ShouldNotBeEmpty();
        PluginBootException.ExitCode.ShouldBe(78);
    }

    [Fact]
    public void Collects_every_manifest_error()
    {
        var root = TempRoot();
        Directory.CreateDirectory(Path.Combine(root, "A"));
        Directory.CreateDirectory(Path.Combine(root, "B"));
        File.WriteAllText(Path.Combine(root, "A", "plugin.json"), "{");
        File.WriteAllText(Path.Combine(root, "B", "plugin.json"), """{"id":1}""");
        var ex = Should.Throw<PluginBootException>(() => Boot(root));
        ex.Errors.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Circular_depends_on_fails()
    {
        var root = TempRoot();
        WriteManifest(root, "A", depends: "B");
        WriteManifest(root, "B", depends: "A");
        var ex = Should.Throw<PluginBootException>(() => Boot(root));
        ex.Errors.ShouldContain(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_assembly_fails()
    {
        var root = TempRoot();
        WriteManifest(root, "Ghost", assembly: "DoesNotExist.dll");
        var ex = Should.Throw<PluginBootException>(() => Boot(root));
        ex.Errors.ShouldContain(e => e.Contains("missing assembly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_entry_type_fails()
    {
        var staged = Stage(typeof(OkPlugin), File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.Ok")));
        var jsonPath = Directory.GetFiles(staged, "plugin.json", SearchOption.AllDirectories).Single();
        var json = File.ReadAllText(jsonPath).Replace("Burbuja.Plugins.Test.Ok.OkPlugin", "Burbuja.Plugins.Test.Ok.MissingType");
        File.WriteAllText(jsonPath, json);
        var ex = Should.Throw<PluginBootException>(() => Boot(staged));
        ex.Errors.ShouldContain(e => e.Contains("instantiate", StringComparison.OrdinalIgnoreCase) || e.Contains("entry_type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Contracts_version_mismatch_fails()
    {
        var staged = Stage(typeof(OkPlugin), File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.Ok")).Replace("\"contracts_version\": \"1.0.0\"", "\"contracts_version\": \"0.0.1\""));
        var ex = Should.Throw<PluginBootException>(() => Boot(staged));
        ex.Errors.ShouldContain(e => e.Contains("contracts_version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plugin_referencing_Burbuja_Core_fails()
    {
        var staged = Stage(typeof(CoreRefPlugin), File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.CoreRef")));
        var ex = Should.Throw<PluginBootException>(() => Boot(staged));
        ex.Errors.ShouldContain(e => e.Contains("Burbuja.Core", StringComparison.Ordinal));
    }

    [Fact]
    public void Policy_rules_without_trusted_fail()
    {
        var json = File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.Ok"))
            .Replace("\"trusted\": false", "\"trusted\": false")
            .Replace("\"provides\": []", "\"provides\": [\"policy.rules\"]");
        var staged = Stage(typeof(OkPlugin), json);
        var ex = Should.Throw<PluginBootException>(() => Boot(staged, trusted: []));
        ex.Errors.ShouldContain(e => e.Contains("trusted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Capability_secrets_without_trusted_plugins_list_fails()
    {
        var json = File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.Ok"))
            .Replace("\"requires\": []", "\"requires\": [\"Secrets\"]")
            .Replace("\"trusted\": false", "\"trusted\": true");
        var staged = Stage(typeof(OkPlugin), json);
        var ex = Should.Throw<PluginBootException>(() => Boot(staged, trusted: []));
        ex.Errors.ShouldContain(e => e.Contains("TrustedPlugins", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Static_registration_path_boots()
    {
        var services = new ServiceCollection();
        var host = new PluginHost(
            new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Options.Options.Create(new KernelOptions { Role = "control-plane", PluginsDirectory = TempRoot() }),
            NullLogger<PluginHost>.Instance);
        host.Boot(services, [new OkPlugin()]);
        host.Loaded.ShouldHaveSingleItem().Manifest.Id.ShouldBe("Test.Ok");
    }

    [Fact]
    public void Unknown_extension_point_fails_boot()
    {
        var plugin = new NamedPlugin("Ext.Unknown", ["does.not.exist"], []);
        var ex = Should.Throw<PluginBootException>(() => BootStatic(plugin));
        ex.Errors.ShouldContain(e => e.Contains("unknown extension_points", StringComparison.Ordinal));
    }

    [Fact]
    public void Extension_point_requires_declared_capabilities()
    {
        var registry = new ExtensionPointRegistry();
        registry.Declare<IClock>("test.clock", ExtensionCardinality.Optional, PluginCapability.ProcessExecution);
        var plugin = new NamedPlugin("Ext.Caps", ["test.clock"], []);
        var ex = Should.Throw<PluginBootException>(() => BootStatic(plugin, registry));
        ex.Errors.ShouldContain(e => e.Contains("requires capabilities", StringComparison.Ordinal));
    }

    [Fact]
    public void Extension_point_cardinality_one_or_more_fails_when_unregistered()
    {
        var plugin = new NamedPlugin("Ext.Empty", ["policy.rules"], []);
        var ex = Should.Throw<PluginBootException>(() => BootStatic(plugin));
        ex.Errors.ShouldContain(e => e.Contains("cardinality", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddBurbujaCore_uses_a_real_plugin_logger_and_split_health_checks()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = TempRoot(),
            ["Kernel:NodeId"] = "test-node",
            ["Auth:ApiKeys:0:Key"] = "k",
            ["Auth:ApiKeys:0:PrincipalId"] = "p",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(cfg);
        var sp = services.BuildServiceProvider();
        var host = sp.GetRequiredService<PluginHost>();
        host.Log.ShouldNotBeOfType<NullLogger<PluginHost>>();
        var registrations = services.Where(d => d.ServiceType.FullName == "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService")
            .ToList();
        registrations.ShouldNotBeEmpty();
        var opts = sp.GetRequiredService<IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>().Value;
        var names = opts.Registrations.Select(r => r.Name).ToArray();
        names.ShouldContain("live");
        names.ShouldContain("ready");

        // Track 3a REPLACED the single always-Healthy `plugins` check with one `plugin:<Id>` check per
        // loaded plugin, tagged `plugins`. This host loads none (empty plugins directory), so the
        // honest assertion here is that the process-level pair is exactly what it was and that no
        // aggregate plugin check survives to answer "healthy" on behalf of plugins it never looked at.
        // The per-plugin facts live in Plugin/PluginHealthTests.cs, which stages real plugins.
        names.ShouldNotContain("plugins", "aggregation is per plugin now, not one optimistic roll-up");
        names.Length.ShouldBe(2, "this host loaded no plugins, so only live and ready are registered");
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

    private sealed class NamedPlugin(
        string id,
        IReadOnlyList<string> extensionPoints,
        IReadOnlyList<PluginCapability> requires) : IBurbujaPlugin
    {
        public PluginManifest Manifest { get; } = new(
            id, id, "1.0.0", PluginContracts.Version, id + ".dll", "X.Y",
            PluginLoadKind.Static, [], extensionPoints, requires, ["all"], null, false, [], 1);

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }

    [Fact]
    public void ThirdParty_plugin_boots_and_resolves_shared_prefix_dependency()
    {
        // The test host does not ship Microsoft.Extensions.Zzz; it exists only in the plugin
        // folder. Before the B1 fallback this failed with FileNotFoundException.
        var staged = StageAllFromDir(
            "Burbuja.Plugins.Test.ThirdParty",
            File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.ThirdParty")));
        var host = Boot(staged);
        var loaded = host.Loaded.ShouldHaveSingleItem();

        loaded.Instance.GetType().GetField("Probe")!.GetValue(null).ShouldBe("zzz");

        var iface = loaded.Instance.GetType().GetInterfaces().Single(i => i.FullName == typeof(IBurbujaPlugin).FullName);
        ReferenceEquals(iface.Assembly, typeof(IBurbujaPlugin).Assembly).ShouldBeTrue();
    }

    [Fact]
    public void ThirdParty_plugin_resolves_native_library_from_plugin_folder()
    {
        var staged = StageAllFromDir(
            "Burbuja.Plugins.Test.ThirdParty",
            File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.ThirdParty")));
        var nativeDir = Directory.GetFiles(staged, "libpomnibot_test.dylib", SearchOption.AllDirectories);
        Assert.SkipUnless(nativeDir.Length > 0, "clang unavailable: native fixture not built");

        var host = Boot(staged);
        var loaded = host.Loaded.ShouldHaveSingleItem();
        var add = loaded.Instance.GetType().GetMethod("Add")!;
        add.Invoke(null, [3, 4]).ShouldBe(7);
    }

    [Fact]
    public void Shared_prefix_fallback_never_weakens_abstractions_unification()
    {
        var staged = StageAllFromDir(
            "Burbuja.Plugins.Test.ThirdParty",
            File.ReadAllText(FindPluginJson("Burbuja.Plugins.Test.ThirdParty")));
        var pluginDir = Directory.GetFiles(staged, "Burbuja.Plugins.Test.ThirdParty.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Single()!;

        // A plugin that ships its own copy of the contract assembly must not get that copy
        // loaded: Burbuja.*Abstractions* is strictly unified, with no resolver fallback.
        File.Copy(typeof(IBurbujaPlugin).Assembly.Location, Path.Combine(pluginDir, "Burbuja.Core.Abstractions.dll"), true);

        var host = Boot(staged);
        var loaded = host.Loaded.ShouldHaveSingleItem();
        var iface = loaded.Instance.GetType().GetInterfaces().Single(i => i.FullName == typeof(IBurbujaPlugin).FullName);
        ReferenceEquals(iface.Assembly, typeof(IBurbujaPlugin).Assembly).ShouldBeTrue();
    }

    [Fact]
    public void Core_assembly_does_not_reference_Agents()
    {
        foreach (var name in typeof(TimeProviderClock).Assembly.GetReferencedAssemblies())
        {
            name.Name.ShouldNotBeNull();
            name.Name.StartsWith("Burbuja.Agents", StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    private static PluginHost Boot(string pluginRoot, string[]? trusted = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = pluginRoot,
            ["Kernel:ContractsVersion"] = PluginContracts.Version,
        }).Build();
        var kernel = new KernelOptions
        {
            Role = "control-plane",
            PluginsDirectory = pluginRoot,
            TrustedPlugins = trusted ?? ["Policy.Core"],
            ContractsVersion = PluginContracts.Version,
        };
        var host = new PluginHost(config, Microsoft.Extensions.Options.Options.Create(kernel), NullLogger<PluginHost>.Instance);
        host.Boot(new ServiceCollection());
        return host;
    }

    private static string Stage(Type pluginType, string pluginJson)
    {
        var root = TempRoot();
        var dir = Path.Combine(root, pluginType.Assembly.GetName().Name!);
        Directory.CreateDirectory(dir);
        var srcDir = Path.GetDirectoryName(pluginType.Assembly.Location)!;
        foreach (var file in Directory.GetFiles(srcDir, Path.GetFileNameWithoutExtension(pluginType.Assembly.Location) + ".*"))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), pluginJson);
        return root;
    }

    // Stages a plugin that is not referenced by this test project (so its private dependencies
    // stay out of the test host's default ALC). Copies the whole build output.
    private static string StageAllFromDir(string projectName, string pluginJson)
    {
        var built = FindBuiltPluginDir(projectName);
        var root = TempRoot();
        var dir = Path.Combine(root, projectName);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(built, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(built, file);
            if (rel.Split(Path.DirectorySeparatorChar).Contains("publish", StringComparer.Ordinal))
                continue;
            var target = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        File.WriteAllText(Path.Combine(dir, "plugin.json"), pluginJson);
        return root;
    }

    private static string FindBuiltPluginDir(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var projectDir = Path.Combine(dir.FullName, "tests", "plugins", projectName);
            if (Directory.Exists(projectDir))
            {
                // A `dotnet publish` of the Host leaves bin/<cfg>/net10.0/publish copies behind; a
                // publish output is not a plugin folder (it is `dotnet build` output + plugin.json,
                // see docs/02-lessons.md), and staging it puts two copies of the same dll in the
                // temp tree, which breaks the single-dll assertions below.
                var dll = Directory.GetFiles(projectDir, projectName + ".dll", SearchOption.AllDirectories)
                    .Where(p => p.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                && !p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (dll is not null)
                    return Path.GetDirectoryName(dll)!;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(projectName);
    }

    private static void WriteManifest(string root, string id, string? depends = null, string assembly = "Missing.dll")
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "contracts_version": "1.0.0",
              "assembly": "{{assembly}}",
              "entry_type": "X.Y",
              "load": "dynamic",
              "provides": [],
              "extension_points": [],
              "requires": [],
              "roles": ["all"],
              "config_section": null,
              "trusted": false,
              "depends_on": [{{(depends is null ? "" : $"\"{depends}\"")}}],
              "load_order": 1
            }
            """);
    }

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-plugintest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindPluginJson(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "plugins", projectName, "plugin.json");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(projectName);
    }
}
