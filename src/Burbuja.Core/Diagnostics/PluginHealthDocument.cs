using System.Globalization;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Plugin;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Burbuja.Core.Diagnostics;

/// <summary>
/// The wire shape of <c>/health/plugins</c>: the overall health status, the identity of the process
/// that answered, and one entry per loaded plugin. Kept next to <see cref="KernelHostInfo"/> and
/// apart from the endpoint so the shape is one reviewable thing.
/// </summary>
public static class PluginHealthDocument
{
    public static JsonObject Build(HealthStatus overall, PluginHealthSnapshot snapshot, KernelHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(host);

        var plugins = new JsonArray();
        foreach (var plugin in snapshot.Plugins)
            plugins.Add(Entry(plugin));

        return new JsonObject
        {
            ["status"] = Word(overall),
            ["host"] = new JsonObject
            {
                ["nodeId"] = host.NodeId,
                ["role"] = host.Role,
                ["pid"] = host.Pid,
                ["startedAt"] = host.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["machineName"] = host.MachineName,
                ["os"] = host.OS,
                ["arch"] = host.Arch,
                ["framework"] = host.Framework,
                ["version"] = host.Version,
                ["workingSetBytes"] = host.WorkingSetBytes,
                ["pluginsDirectory"] = host.PluginsDirectory,
            },

            // PluginLoadContext is a non-collectible AssemblyLoadContext: its assemblies can never be
            // unloaded, so "reload a plugin in place" is not a thing this kernel can do. The field is
            // stated rather than omitted so a client does not read its absence as "unknown".
            ["reloadable"] = false,
            ["plugins"] = plugins,
        };
    }

    private static JsonObject Entry(PluginHealthEntrySnapshot plugin) => new()
    {
        ["id"] = plugin.Id,
        ["name"] = plugin.Name,
        ["version"] = plugin.Version,
        ["load"] = plugin.Load.ToString(),
        ["trusted"] = plugin.Trusted,
        ["provides"] = new JsonArray(plugin.Provides.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["requires"] = new JsonArray(plugin.Requires.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["hostedServices"] = new JsonArray(plugin.HostedServices.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
        ["status"] = Word(plugin.Status),
        ["description"] = plugin.Description,
        ["data"] = plugin.Data?.DeepClone(),

        // The supervisor state is the raw lifecycle fact ("Started", "Faulted"); status is the
        // folded health verdict. Both are useful: one says what happened, the other what it means.
        ["state"] = plugin.State.ToString(),
        ["startedAt"] = plugin.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["error"] = plugin.Error,
    };

    private static string Word(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => nameof(HealthStatus.Healthy),
        HealthStatus.Degraded => nameof(HealthStatus.Degraded),
        _ => nameof(HealthStatus.Unhealthy),
    };

    private static string Word(PluginHealthStatus status) => status switch
    {
        PluginHealthStatus.Healthy => nameof(PluginHealthStatus.Healthy),
        PluginHealthStatus.Degraded => nameof(PluginHealthStatus.Degraded),
        _ => nameof(PluginHealthStatus.Unhealthy),
    };
}
