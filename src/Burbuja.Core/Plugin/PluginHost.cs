using System.Reflection;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Plugin;

public sealed record LoadedPlugin(PluginManifest Manifest, IBurbujaPlugin Instance, Assembly Assembly, PluginLoadKind EffectiveLoad);

public sealed class PluginHost
{
    private readonly IConfiguration _config;
    private readonly KernelOptions _kernel;
    private readonly ILogger<PluginHost> _log;
    private readonly ExtensionPointRegistry _registry;
    private readonly PluginHealthRegistry _health;
    private readonly List<LoadedPlugin> _loaded = [];

    internal ILogger<PluginHost> Log => _log;
    public IExtensionPointRegistry ExtensionPoints => _registry;

    /// <summary>Track 3a: per-plugin health, fed by the supervised hosted services and the health checks.</summary>
    public PluginHealthRegistry Health => _health;

    public PluginHost(
        IConfiguration config,
        IOptions<KernelOptions> kernel,
        ILogger<PluginHost> log,
        IExtensionPointRegistry? registry = null,
        PluginHealthRegistry? health = null)
    {
        _config = config;
        _kernel = kernel.Value;
        _log = log;
        _registry = registry as ExtensionPointRegistry ?? new ExtensionPointRegistry();
        _health = health ?? new PluginHealthRegistry();
        if (!_registry.TryGet("policy.rules", out _))
            _registry.Declare<Burbuja.Core.Abstractions.Policy.IPolicyRule<Burbuja.Core.Abstractions.Policy.SegmentPolicySubject>>(
                "policy.rules", ExtensionCardinality.OneOrMore);
    }

    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    public void Boot(IServiceCollection services, IReadOnlyList<IBurbujaPlugin>? staticPlugins = null)
    {
        var errors = new List<string>();

        // 1. discover
        var discovered = Discover(staticPlugins, errors);

        // 2. validate all manifests (collect every error)
        var valid = new List<(PluginManifest Manifest, string? Directory, IBurbujaPlugin? Static)>();
        foreach (var item in discovered)
        {
            if (item.Manifest is { } manifest)
                valid.Add((manifest, item.Directory, item.Static));
        }

        // 3. filter by role + Plugins:<Id>:Enabled
        var filtered = valid.Where(v => IsEnabled(v.Manifest)).ToList();

        var duplicates = filtered.GroupBy(v => v.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToArray();
        foreach (var group in duplicates)
            errors.Add($"duplicate plugin id '{group.Key}': {string.Join(", ", group.Select(v => v.Directory is null
                ? $"static {v.Manifest.Assembly}" : Path.Combine(v.Directory, "plugin.json")))}");

        // Validate installed files before sorting. A missing DLL must identify the installed
        // manifest, even when a static implementation of that id is also linked into the host.
        foreach (var item in filtered.Where(v => v.Directory is not null))
        {
            var dll = Path.Combine(item.Directory!, item.Manifest.Assembly);
            if (!File.Exists(dll))
                errors.Add($"plugin {item.Manifest.Id} ({Path.Combine(item.Directory!, "plugin.json")}): missing assembly '{dll}'");
        }

        if (duplicates.Length > 0)
            throw new PluginBootException(errors);

        // 4. topo-sort
        List<(PluginManifest Manifest, string? Directory, IBurbujaPlugin? Static)> ordered;
        try
        {
            ordered = TopoSort(filtered, errors);
        }
        catch (PluginBootException)
        {
            throw;
        }

        if (errors.Count > 0)
            throw new PluginBootException(errors);

        var loadedAssemblies = new List<(PluginManifest Manifest, Assembly Assembly, IBurbujaPlugin? Static, PluginLoadKind Kind)>();

        // 5. load
        foreach (var item in ordered)
        {
            try
            {
                if (item.Static is not null)
                {
                    loadedAssemblies.Add((item.Manifest, item.Static.GetType().Assembly, item.Static, PluginLoadKind.Static));
                    continue;
                }

                var dir = item.Directory ?? throw new InvalidOperationException(item.Manifest.Id);
                var dll = Path.Combine(dir, item.Manifest.Assembly);
                if (!File.Exists(dll))
                {
                    errors.Add($"plugin {item.Manifest.Id}: missing assembly '{dll}'");
                    continue;
                }

                Assembly assembly;
                if (item.Manifest.Load == PluginLoadKind.Dynamic)
                {
                    var alc = new PluginLoadContext(dll);
                    assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
                }
                else
                {
                    assembly = Assembly.LoadFrom(dll);
                }

                loadedAssemblies.Add((item.Manifest, assembly, null, item.Manifest.Load));
            }
            catch (Exception ex)
            {
                errors.Add($"plugin {item.Manifest.Id}: load failed ({ex.Message})");
            }
        }

        // 6. guards
        foreach (var item in loadedAssemblies.ToList())
        {
            if (item.Manifest.ContractsVersion != _kernel.ContractsVersion
                && item.Manifest.ContractsVersion != PluginContracts.Version)
            {
                errors.Add($"plugin {item.Manifest.Id}: contracts_version '{item.Manifest.ContractsVersion}' incompatible with '{_kernel.ContractsVersion}'");
            }

            if (item.Static is null && PluginManifestReader.AssemblyReferencesForbidden(item.Assembly, out var forbidden))
                errors.Add($"plugin {item.Manifest.Id}: must not reference {forbidden}");

            var iface = item.Assembly.GetType(item.Manifest.EntryType)
                ?.GetInterfaces()
                .FirstOrDefault(i => i.FullName == typeof(IBurbujaPlugin).FullName);
            if (iface is not null && !ReferenceEquals(iface.Assembly, typeof(IBurbujaPlugin).Assembly))
                errors.Add($"plugin {item.Manifest.Id}: IBurbujaPlugin type identity mismatch (ALC copied contracts)");
        }

        if (errors.Count > 0)
            throw new PluginBootException(errors);

        // 7. instantiate + cross-check manifest
        var instances = new List<LoadedPlugin>();
        foreach (var item in loadedAssemblies)
        {
            try
            {
                IBurbujaPlugin plugin;
                if (item.Static is not null)
                {
                    plugin = item.Static;
                }
                else
                {
                    var type = item.Assembly.GetType(item.Manifest.EntryType)
                        ?? throw new InvalidOperationException($"entry_type '{item.Manifest.EntryType}' not found");
                    plugin = (IBurbujaPlugin)(Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException("activator returned null"));
                }

                // No ConfigureServices runs before a mismatch fails boot. Static code may be
                // explicitly staged as dynamic; all other declarations must agree.
                if (ManifestDiff(item.Manifest, plugin.Manifest) is { Length: > 0 } diff)
                    errors.Add($"plugin {item.Manifest.Id}: manifest cross-check failed ({diff})");

                instances.Add(new LoadedPlugin(item.Manifest, plugin, item.Assembly, item.Kind));
            }
            catch (Exception ex)
            {
                errors.Add($"plugin {item.Manifest.Id}: instantiate failed ({ex.Message})");
            }
        }

        // 8. capability grants
        var trusted = new HashSet<string>(_kernel.TrustedPlugins, StringComparer.OrdinalIgnoreCase);
        foreach (var p in instances)
        {
            var needsTrust = p.Manifest.Provides.Any(x =>
                    x.Contains("policy", StringComparison.OrdinalIgnoreCase)
                    || x.Contains("arbiter", StringComparison.OrdinalIgnoreCase)
                    || x.Contains("redact", StringComparison.OrdinalIgnoreCase))
                || p.Manifest.Requires.Contains(PluginCapability.Secrets)
                || p.Manifest.Requires.Contains(PluginCapability.BackgroundService);

            if (needsTrust && (!p.Manifest.Trusted || !trusted.Contains(p.Manifest.Id)))
                errors.Add($"plugin {p.Manifest.Id}: policy/arbiter/redaction/secrets/background require trusted + Kernel:TrustedPlugins");
        }

        if (errors.Count > 0)
            throw new PluginBootException(errors);

        // 8a. plugin-declared extension points (before manifest validation, so order never matters)
        foreach (var p in instances)
        {
            try
            {
                p.Instance.DeclareExtensionPoints(_registry);
            }
            catch (Exception ex)
            {
                errors.Add($"plugin {p.Manifest.Id}: DeclareExtensionPoints failed ({ex.Message})");
            }
        }

        if (errors.Count > 0)
            throw new PluginBootException(errors);

        // 8b. extension points exist, contract known, required capabilities declared
        foreach (var p in instances)
        {
            var pluginCaps = p.Manifest.Requires.Aggregate(PluginCapability.None, (a, c) => a | c);
            foreach (var name in p.Manifest.ExtensionPoints)
            {
                if (!_registry.TryGet(name, out var point))
                {
                    errors.Add($"plugin {p.Manifest.Id}: unknown extension_points entry '{name}'");
                    continue;
                }

                if ((point.RequiredCapabilities & pluginCaps) != point.RequiredCapabilities)
                {
                    errors.Add(
                        $"plugin {p.Manifest.Id}: extension point '{name}' requires capabilities {point.RequiredCapabilities}");
                }
            }
        }

        if (errors.Count > 0)
            throw new PluginBootException(errors);

        // 9. bind options (ValidateOnStart) — plugin config sections
        foreach (var p in instances)
        {
            if (string.IsNullOrEmpty(p.Manifest.ConfigSection))
                continue;
            var section = _config.GetSection(p.Manifest.ConfigSection);
            if (!section.Exists())
                _log.LogDebug("plugin {Id} config section {Section} is empty", p.Manifest.Id, p.Manifest.ConfigSection);
            services.AddOptions<PluginSectionOptions>(p.Manifest.Id)
                .Bind(section)
                .ValidateOnStart();
        }

        // 10. ConfigureServices into tagged collection, then supervise exactly the hosted services
        //     this plugin registered. The service-collection count around the call is the ownership
        //     proof: everything inside [start, end) was added by this plugin's ConfigureServices, so
        //     the Host's and the other plugins' IHostedService descriptors are never touched. They
        //     are registered before Boot (Core) or after it (Program.cs), so they always fall
        //     outside every plugin's range.
        // M14 R10 (request row X5) — the ranges are kept, not only used: step 11b asks which plugin
        // registered an IHttpIngressEndpoint, and the range is the only exact answer (a descriptor's type
        // says what it is, never whose it is).
        var ranges = new List<(PluginManifest Manifest, int Start, int End)>();
        foreach (var p in instances)
        {
            _health.Register(p.Manifest);
            var start = services.Count;
            p.Instance.ConfigureServices(services);
            var end = services.Count;
            ranges.Add((p.Manifest, start, end));
            var failSoft = bool.TryParse(_config[$"Plugins:{p.Manifest.Id}:FailSoft"], out var soft) && soft;
            for (var i = start; i < end; i++)
            {
                if (services[i].ServiceType != typeof(IHostedService))
                    continue;
                services[i] = SupervisedHostedService.Wrap(services[i], p.Manifest.Id, _health, _log, failSoft);
            }
        }

        _loaded.Clear();
        _loaded.AddRange(instances);

        // 11. kernel validation (Core-owned checks; agent-specific checks run when those services exist)
        var ids = _loaded.Select(p => p.Manifest.Id).ToList();
        if (ids.Count != ids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            errors.Add("duplicate plugin id");

        var ruleTypes = services
            .Where(d => d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericTypeDefinition() == typeof(Burbuja.Core.Abstractions.Policy.IPolicyRule<>))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .ToList();
        foreach (var t in ruleTypes)
        {
            if (t!.GetConstructors().All(c => c.GetParameters().Length > 0))
                errors.Add($"policy rule {t.FullName} is not stateless (requires constructor injection)");
        }

        var extensionRegistrations = services
            .Where(d => d.ServiceType == typeof(ExtensionRegistration) && d.ImplementationInstance is ExtensionRegistration)
            .Select(d => (ExtensionRegistration)d.ImplementationInstance!)
            .ToList();

        foreach (var point in _registry.All)
        {
            var listed = _loaded.Any(p => p.Manifest.ExtensionPoints.Contains(point.Name, StringComparer.Ordinal));
            if (!listed)
                continue;
            var byPoint = extensionRegistrations.Count(r => string.Equals(r.Point, point.Name, StringComparison.Ordinal));
            var count = byPoint > 0 ? byPoint : CountContract(services, point.Contract);
            var ok = point.Cardinality switch
            {
                ExtensionCardinality.ExactlyOne => count == 1,
                ExtensionCardinality.OneOrMore => count >= 1,
                ExtensionCardinality.Optional => count <= 1,
                _ => true,
            };
            if (!ok)
                errors.Add($"extension point '{point.Name}' cardinality {point.Cardinality} not satisfied (count={count})");
        }

        // 11b. HTTP ingress doors — the `HttpEndpoints` capability is a two-way promise, and this is the
        //      first thing in the tree that keeps it (M14 R10, request row X5). A plugin that declares the
        //      capability must register at least one door, and a door registered by a plugin that did not
        //      declare it is refused: the manifest is what an owner reads before trusting a plugin, so a
        //      door that is not in it is a door nobody agreed to. Which door belongs to whom is answered by
        //      the descriptor RANGE step 10 recorded, not by the endpoint's own PluginId property — a
        //      plugin cannot borrow another's door by naming it.
        foreach (var (manifest, start, end) in ranges)
        {
            var declared = manifest.Requires.Contains(PluginCapability.HttpEndpoints);
            var doors = 0;
            for (var i = start; i < end; i++)
            {
                if (services[i].ServiceType == typeof(Burbuja.Core.Abstractions.Ingress.IHttpIngressEndpoint))
                    doors++;
            }

            if (declared && doors == 0)
            {
                errors.Add(
                    $"plugin {manifest.Id}: requires HttpEndpoints and registers no IHttpIngressEndpoint — the "
                    + "capability promises at least one door at /ingress/{route}");
            }
            else if (!declared && doors > 0)
            {
                errors.Add(
                    $"plugin {manifest.Id}: registers {doors} IHttpIngressEndpoint without the HttpEndpoints "
                    + "capability in its manifest");
            }
        }

        // 12. ValidateOnBuild happens when the host builds the provider
        if (errors.Count > 0)
            throw new PluginBootException(errors);

        // 13. start in order — the generic host starts the supervised IHostedService wrappers, which
        //     fail the boot (fail-fast) or record the failure (Plugins:<Id>:FailSoft=true)
        // 14. one `plugin:<id>` health check per loaded plugin is registered by AddBurbujaCore
    }

    private List<(PluginManifest? Manifest, string? Directory, IBurbujaPlugin? Static)> Discover(
        IReadOnlyList<IBurbujaPlugin>? staticPlugins,
        List<string> errors)
    {
        var list = new List<(PluginManifest? Manifest, string? Directory, IBurbujaPlugin? Static)>();
        if (staticPlugins is not null)
        {
            foreach (var p in staticPlugins)
                list.Add((p.Manifest, null, p));
        }

        var root = _kernel.PluginsDirectory;
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var json = Path.Combine(dir, "plugin.json");
                if (!File.Exists(json))
                    continue;
                var (manifest, errs) = PluginManifestReader.Read(json);
                errors.AddRange(errs);
                list.Add((manifest, dir, null));
            }
        }

        return list;
    }

    private bool IsEnabled(PluginManifest manifest)
    {
        var role = _kernel.Role;
        if (manifest.Roles.Count > 0
            && !manifest.Roles.Contains("all", StringComparer.OrdinalIgnoreCase)
            && !manifest.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return false;

        var enabledCsv = _kernel.EnabledPlugins;
        if (!string.IsNullOrWhiteSpace(enabledCsv))
        {
            var set = enabledCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!set.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        var flag = _config[$"Plugins:{manifest.Id}:Enabled"];
        if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static List<(PluginManifest Manifest, string? Directory, IBurbujaPlugin? Static)> TopoSort(
        List<(PluginManifest Manifest, string? Directory, IBurbujaPlugin? Static)> items,
        List<string> errors)
    {
        // Duplicate identities were reported with their install locations before this method.
        var byId = items.ToDictionary(i => i.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(PluginManifest Manifest, string? Directory, IBurbujaPlugin? Static)>();

        void Visit(string id)
        {
            if (visited.Contains(id))
                return;
            if (!visiting.Add(id))
            {
                errors.Add($"plugin dependency cycle involving '{id}'");
                return;
            }

            if (!byId.TryGetValue(id, out var item))
            {
                errors.Add($"plugin depends on missing id '{id}'");
                visiting.Remove(id);
                return;
            }

            foreach (var dep in item.Manifest.DependsOn)
                Visit(dep);

            visiting.Remove(id);
            visited.Add(id);
            result.Add(item);
        }

        foreach (var item in items.OrderBy(i => i.Manifest.LoadOrder).ThenBy(i => i.Manifest.Id, StringComparer.Ordinal))
            Visit(item.Manifest.Id);

        return result;
    }

    private static int CountContract(IServiceCollection services, Type contract)
    {
        if (contract.IsGenericTypeDefinition)
        {
            return services.Count(d =>
                d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == contract);
        }

        return services.Count(d => d.ServiceType == contract);
    }

    private static string ManifestDiff(PluginManifest disk, PluginManifest code)
    {
        var diffs = new List<string>();
        if (!string.Equals(disk.Id, code.Id, StringComparison.Ordinal))
            diffs.Add($"id disk='{disk.Id}' code='{code.Id}'");
        if (!string.Equals(disk.EntryType, code.EntryType, StringComparison.Ordinal))
            diffs.Add($"entry_type disk='{disk.EntryType}' code='{code.EntryType}'");
        if (!string.Equals(disk.Name, code.Name, StringComparison.Ordinal))
            diffs.Add($"name disk='{disk.Name}' code='{code.Name}'");
        if (!string.Equals(disk.Version, code.Version, StringComparison.Ordinal))
            diffs.Add($"version disk='{disk.Version}' code='{code.Version}'");
        if (!string.Equals(disk.ContractsVersion, code.ContractsVersion, StringComparison.Ordinal))
            diffs.Add($"contracts_version disk='{disk.ContractsVersion}' code='{code.ContractsVersion}'");
        if (!string.Equals(disk.Assembly, code.Assembly, StringComparison.Ordinal))
            diffs.Add($"assembly disk='{disk.Assembly}' code='{code.Assembly}'");
        if (disk.Load != code.Load && !(disk.Load == PluginLoadKind.Dynamic && code.Load == PluginLoadKind.Static))
            diffs.Add($"load disk='{disk.Load}' code='{code.Load}'");
        if (!disk.Provides.SequenceEqual(code.Provides, StringComparer.Ordinal))
            diffs.Add($"provides disk=[{string.Join(",", disk.Provides)}] code=[{string.Join(",", code.Provides)}]");
        if (!disk.ExtensionPoints.SequenceEqual(code.ExtensionPoints, StringComparer.Ordinal))
            diffs.Add($"extension_points disk=[{string.Join(",", disk.ExtensionPoints)}] code=[{string.Join(",", code.ExtensionPoints)}]");
        if (!disk.Requires.SequenceEqual(code.Requires))
            diffs.Add($"requires disk=[{string.Join(",", disk.Requires)}] code=[{string.Join(",", code.Requires)}]");
        if (!disk.Roles.SequenceEqual(code.Roles, StringComparer.Ordinal))
            diffs.Add($"roles disk=[{string.Join(",", disk.Roles)}] code=[{string.Join(",", code.Roles)}]");
        if (!string.Equals(disk.ConfigSection, code.ConfigSection, StringComparison.Ordinal))
            diffs.Add($"config_section disk='{disk.ConfigSection}' code='{code.ConfigSection}'");
        if (disk.Trusted != code.Trusted)
            diffs.Add($"trusted disk='{disk.Trusted}' code='{code.Trusted}'");
        if (!disk.DependsOn.SequenceEqual(code.DependsOn, StringComparer.Ordinal))
            diffs.Add($"depends_on disk=[{string.Join(",", disk.DependsOn)}] code=[{string.Join(",", code.DependsOn)}]");
        if (disk.LoadOrder != code.LoadOrder)
            diffs.Add($"load_order disk='{disk.LoadOrder}' code='{code.LoadOrder}'");
        return string.Join("; ", diffs);
    }
}
