using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Plugin;
using Json.Schema;

namespace Burbuja.Core.Plugin;

internal sealed record PluginManifestFile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contracts_version")] string ContractsVersion,
    [property: JsonPropertyName("assembly")] string Assembly,
    [property: JsonPropertyName("entry_type")] string EntryType,
    [property: JsonPropertyName("load")] string Load,
    [property: JsonPropertyName("provides")] IReadOnlyList<string> Provides,
    [property: JsonPropertyName("extension_points")] IReadOnlyList<string> ExtensionPoints,
    [property: JsonPropertyName("requires")] IReadOnlyList<string> Requires,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("config_section")] string? ConfigSection,
    [property: JsonPropertyName("trusted")] bool Trusted,
    [property: JsonPropertyName("depends_on")] IReadOnlyList<string> DependsOn,
    [property: JsonPropertyName("load_order")] int LoadOrder);

internal static class PluginManifestReader
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        var names = typeof(PluginManifestReader).Assembly.GetManifestResourceNames();
        var resource = names.FirstOrDefault(n => n.EndsWith("plugin-manifest.schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("embedded plugin-manifest.schema.json missing");
        using var stream = typeof(PluginManifestReader).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(resource);
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public static (PluginManifest? Manifest, List<string> Errors) Read(string pluginJsonPath)
    {
        var errors = new List<string>();
        if (!File.Exists(pluginJsonPath))
        {
            errors.Add($"{pluginJsonPath}: missing plugin.json");
            return (null, errors);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(pluginJsonPath));
        }
        catch (Exception ex)
        {
            errors.Add($"{pluginJsonPath}: invalid JSON ({ex.Message})");
            return (null, errors);
        }

        var eval = Schema.Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!eval.IsValid)
        {
            foreach (var detail in (eval.Details ?? []).Where(d => !d.IsValid && d.Errors is { Count: > 0 }))
            {
                var msg = detail.Errors is null
                    ? "schema violation"
                    : string.Join("; ", detail.Errors.Select(e => e.Value));
                errors.Add($"{pluginJsonPath}: {detail.InstanceLocation} {msg}");
            }

            if (errors.Count == 0)
                errors.Add($"{pluginJsonPath}: plugin.json failed schema validation");
            return (null, errors);
        }

        var file = JsonSerializer.Deserialize<PluginManifestFile>(node, JsonWire.Options);
        if (file is null)
        {
            errors.Add($"{pluginJsonPath}: could not deserialize plugin.json");
            return (null, errors);
        }

        var caps = new List<PluginCapability>();
        foreach (var raw in file.Requires)
        {
            if (!Enum.TryParse<PluginCapability>(raw, ignoreCase: true, out var cap) || cap == PluginCapability.None)
                errors.Add($"{pluginJsonPath}: unknown capability '{raw}'");
            else
                caps.Add(cap);
        }

        if (!Enum.TryParse<PluginLoadKind>(file.Load, ignoreCase: true, out var loadKind))
        {
            errors.Add($"{pluginJsonPath}: invalid load '{file.Load}'");
            loadKind = PluginLoadKind.Dynamic;
        }

        if (errors.Count > 0)
            return (null, errors);

        var manifest = new PluginManifest(
            file.Id,
            file.Name,
            file.Version,
            file.ContractsVersion,
            file.Assembly,
            file.EntryType,
            loadKind,
            file.Provides,
            file.ExtensionPoints,
            caps,
            file.Roles,
            file.ConfigSection,
            file.Trusted,
            file.DependsOn,
            file.LoadOrder);
        return (manifest, errors);
    }

    public static bool AssemblyReferencesForbidden(Assembly assembly, out string? forbidden)
    {
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            if (n == "Burbuja.Core" || n == "Burbuja.Host")
            {
                forbidden = n;
                return true;
            }
        }

            var location = assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var deps = Path.ChangeExtension(location, ".deps.json");
                if (File.Exists(deps))
                {
                    var text = File.ReadAllText(deps);
                    if (text.Contains("\"Burbuja.Core\":", StringComparison.Ordinal)
                        || text.Contains("\"Burbuja.Host\":", StringComparison.Ordinal))
                    {
                        forbidden = "Burbuja.Core";
                        return true;
                    }
                }
            }

        forbidden = null;
        return false;
    }
}
