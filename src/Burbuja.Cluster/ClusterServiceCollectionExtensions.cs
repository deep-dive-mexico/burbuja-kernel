using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Burbuja.Cluster.Controller;
using Burbuja.Cluster.Networks;
using Burbuja.Cluster.Os;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Burbuja.Cluster;

public static class ClusterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cluster module. Inert until <c>Cluster:Enabled</c> is true.
    ///
    /// <para>Everything a deployment may want to vary is a plain service a host or a plugin registers
    /// alongside: an <see cref="INetworkProvider"/> for another network, an <see cref="INodeFactsSource"/> for
    /// another sensor, an <see cref="IWorkloadDriver"/> for another kind of workload with its own payload
    /// schema, an <see cref="IOsExecutionAdapter"/> for another platform or sandbox, and
    /// <see cref="IPlacementFilter"/> or <see cref="IPlacementScorer"/> for placement rules. Providers
    /// registered before this call are consulted before the built-in ones, so a specific network claims an
    /// address before the generic classification sees it.</para>
    /// </summary>
    public static IServiceCollection AddBurbujaCluster(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ClusterOptions.Section).Get<ClusterOptions>() ?? new ClusterOptions();

        // One id per machine: a host that already names itself for the kernel is the same node here.
        if (string.IsNullOrEmpty(options.NodeId) && configuration["Kernel:NodeId"] is { Length: > 0 } kernelNode) options.NodeId = kernelNode;
        options.Validate();
        services.AddSingleton(options);
        if (!options.Enabled) return services;

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INetworkProvider, LoopbackNetwork>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INetworkProvider, LanNetwork>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INetworkProvider, StaticNetwork>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOsExecutionAdapter, MacOsExecutionAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOsExecutionAdapter, LinuxExecutionAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOsExecutionAdapter, PortableExecutionAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INodeFactsSource, SystemFactsSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INodeFactsSource, NvidiaFactsSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkloadDriver, ProcessWorkloadDriver>());
        services.TryAddSingleton<EndpointAdvertiser>();

        if (options.IsController)
        {
            services.TryAddSingleton<ClusterTokens>();
            services.TryAddSingleton<ClusterController>();
            services.TryAddSingleton<IClusterControlPlane>(sp => sp.GetRequiredService<ClusterController>());
            services.AddHostedService<ClusterControllerService>();
        }

        if (options.IsAgent)
        {
            services.TryAddSingleton<InteractiveGate>();
            services.TryAddSingleton<NodePressureMonitor>();
            services.TryAddSingleton<INodePressure>(sp => sp.GetRequiredService<NodePressureMonitor>());
            services.TryAddSingleton<IControllerLink>(sp => options.IsController
                ? new LocalControllerLink(sp.GetRequiredService<ClusterController>())
                : new HttpControllerLink(
                    options,
                    sp.GetServices<INetworkProvider>(),
                    new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
                    {
                        Timeout = Timeout.InfiniteTimeSpan,
                    },
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<HttpControllerLink>>()));
            services.TryAddSingleton(sp => new NodeAgent(
                options,
                sp.GetRequiredService<IControllerLink>(),
                sp.GetServices<INodeFactsSource>(),
                sp.GetServices<IWorkloadDriver>(),
                sp.GetServices<IOsExecutionAdapter>(),
                sp.GetRequiredService<EndpointAdvertiser>(),
                sp.GetRequiredService<InteractiveGate>(),
                sp.GetRequiredService<NodePressureMonitor>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<NodeAgent>>()));
            services.AddHostedService(sp => sp.GetRequiredService<NodeAgent>());
        }

        return services;
    }
}

/// <summary>Publishes the controller's addresses and notices nodes going silent between syncs.</summary>
internal sealed class ClusterControllerService(ClusterController controller, EndpointAdvertiser advertiser, ClusterOptions options, TimeProvider time)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Min(1, options.Controller.SyncIntervalSeconds));
        var advertisedAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (time.GetUtcNow() - advertisedAt > TimeSpan.FromSeconds(60))
                {
                    controller.SetEndpoints(await advertiser.AdvertiseAsync(stoppingToken));
                    advertisedAt = time.GetUtcNow();
                }

                controller.Tick();
                await Task.Delay(tick, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
