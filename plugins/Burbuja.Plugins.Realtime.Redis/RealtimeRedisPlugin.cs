using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Realtime.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Burbuja.Plugins.Realtime.Redis;

/// <summary>
/// Replaces the kernel's in-process <see cref="IRealtimeBackplane"/> with a Redis/Valkey
/// implementation that lets lobby nodes on different machines form one fleet.
/// </summary>
/// <remarks>
/// Configure with <c>Realtime:Redis:Configuration</c> (required — a StackExchange.Redis
/// connection string such as <c>localhost:6379</c> or <c>my-cluster.cache.amazonaws.com:6379,ssl=true</c>)
/// and optionally <c>Realtime:Redis:KeyPrefix</c> (default <c>{burbuja-rt}:</c>).
/// The default prefix is a Redis hash tag: every key lands in the same slot, so multi-key
/// Lua scripts work on Redis Cluster and ElastiCache cluster-mode too.
/// </remarks>
public sealed class RealtimeRedisPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Realtime.Redis",
        "Redis Realtime Backplane",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Realtime.Redis.dll",
        typeof(RealtimeRedisPlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["realtime.backplane.redis"],
        [],
        // Passive plugin with network access; no hosted service.
        [PluginCapability.Network],
        ["all", "control-plane", "worker"],
        "Realtime:Redis",
        true,
        [],
        200);

    public void ConfigureServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IRealtimeBackplane>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connStr = config["Realtime:Redis:Configuration"];
            if (string.IsNullOrWhiteSpace(connStr))
                throw new InvalidOperationException(
                    "The 'Realtime:Redis:Configuration' key is required but was not set. " +
                    "Provide a StackExchange.Redis connection string (e.g. 'localhost:6379').");

            var prefix = config["Realtime:Redis:KeyPrefix"];
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = "{burbuja-rt}:";

            var opts = ConfigurationOptions.Parse(connStr);
            opts.AbortOnConnectFail = false;
            var mux = ConnectionMultiplexer.Connect(opts);
            return new RedisBackplane(mux, prefix, ownsMultiplexer: true);
        }));
    }
}
