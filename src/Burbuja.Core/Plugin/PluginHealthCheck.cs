using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Burbuja.Core.Plugin;

/// <summary>
/// Track 3a: one health check per loaded plugin (registered as <c>plugin:&lt;Id&gt;</c>). It reports
/// the supervisor's registry state first — a start failure or a faulted background service is
/// Unhealthy, "registered but services not started" is Degraded — then folds in every
/// <see cref="IPluginHealthContributor"/> that belongs to this plugin, worst status wins. A
/// contributor that throws is Unhealthy for its own plugin and never for anyone else's.
/// </summary>
public sealed class PluginHealthCheck(
    PluginHealthRegistry registry,
    string pluginId,
    IServiceProvider services) : IHealthCheck
{
    public string PluginId { get; } = pluginId;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = registry.StateHealth(PluginId);

        foreach (var contributor in services.GetServices<IPluginHealthContributor>())
        {
            if (!string.Equals(contributor.PluginId, PluginId, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                health = Worse(health, await contributor.CheckAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                health = new PluginHealth(
                    PluginHealthStatus.Unhealthy,
                    $"health contributor {contributor.GetType().Name} threw {ex.GetType().Name}: {ex.Message}",
                    health.Data);
            }
        }

        registry.RecordHealth(PluginId, health);

        return health.Status switch
        {
            PluginHealthStatus.Healthy => HealthCheckResult.Healthy(health.Description, Data(health.Data)),
            PluginHealthStatus.Degraded => HealthCheckResult.Degraded(health.Description, data: Data(health.Data)),
            _ => HealthCheckResult.Unhealthy(health.Description, data: Data(health.Data)),
        };
    }

    private static PluginHealth Worse(PluginHealth a, PluginHealth b) =>
        b.Status > a.Status ? b : a;

    private static IReadOnlyDictionary<string, object>? Data(JsonObject? data) =>
        data is null or { Count: 0 }
            ? null
            : data.ToDictionary(kv => kv.Key, kv => (object)(kv.Value?.ToJsonString() ?? "null"), StringComparer.Ordinal);
}
