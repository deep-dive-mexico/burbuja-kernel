using Burbuja.Compute.Abstractions;
using Burbuja.Compute.Backends;
using Burbuja.Compute.Handlers;
using Burbuja.Compute.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Recovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Burbuja.Compute;

public sealed class ComputePlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Burbuja.Compute",
        "Burbuja Compute",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Compute.dll",
        typeof(ComputePlugin).FullName!,
        PluginLoadKind.Static,
        [ComputeConstants.Namespace],
        ["compute.backend"],
        [PluginCapability.ProcessExecution, PluginCapability.FilesystemWrite, PluginCapability.BackgroundService],
        ["all", "control-plane", "worker"],
        ComputeOptions.Section,
        true,
        [],
        220);

    /// <summary>The Compute RP owns the compute.backend point.</summary>
    public void DeclareExtensionPoints(IExtensionPointRegistry registry) =>
        registry.Declare<IComputeBackend>(
            "compute.backend",
            ExtensionCardinality.OneOrMore,
            PluginCapability.ProcessExecution | PluginCapability.FilesystemWrite);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<ComputeOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(ComputeOptions.Section).Bind(o));
        services.AddSingleton<IRoleContributor, ComputeRoles>();
        services.AddSingleton<LocalDirectoryBackend>();
        services.AddExtension<IComputeBackend, LocalDirectoryBackend>("compute.backend");
        services.AddSingleton<GitWorktreeBackend>();
        services.AddExtension<IComputeBackend, GitWorktreeBackend>("compute.backend");
        services.AddSingleton<BackendRegistry>();
        services.AddSingleton<ComputeStateStore>();
        services.AddSingleton<IComputeWorkspaceAccess, ComputeWorkspaceAccess>();

        services.AddSingleton<IResourceProvider, ComputeResourceProvider>();
        services.AddSingleton<IResourceHandler<ComputeWorkspaceProperties>, ComputeWorkspaceHandler>();
        services.AddSingleton<IResourceHandler<ComputeSnapshotProperties>, ComputeSnapshotHandler>();
        services.AddSingleton<IResourceHandler<ComputeBackendProjection>, ComputeCatalogHandler>();

        services.AddSingleton<ComputeExecutor>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ComputeExecutor>());
        services.AddSingleton<IEventStreamResolver, ComputeEventStreams>();
        services.AddSingleton<IStreamRecoveryHandler, ComputeReaper>();
    }
}
