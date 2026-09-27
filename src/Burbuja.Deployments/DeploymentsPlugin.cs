using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Arm;
using Burbuja.Deployments.Execution;
using Burbuja.Deployments.Handlers;
using Burbuja.Deployments.Targets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Burbuja.Deployments;

public sealed class DeploymentsPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Burbuja.Deployments",
        "Burbuja Deployments",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Deployments.dll",
        typeof(DeploymentsPlugin).FullName!,
        PluginLoadKind.Static,
        [DeploymentsConstants.Namespace],
        ["deployments.target"],
        [],
        ["all", "control-plane", "worker"],
        DeploymentsOptions.Section,
        false,
        [],
        210);

    /// <summary>The Deployments RP owns the deployments.target point.</summary>
    public void DeclareExtensionPoints(IExtensionPointRegistry registry) =>
        registry.Declare<IDeploymentTarget>("deployments.target", ExtensionCardinality.OneOrMore);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<DeploymentsOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(DeploymentsOptions.Section).Bind(o));
        services.AddSingleton<IRoleContributor, DeploymentsRoles>();
        services.AddSingleton<CancellationRegistry>();
        services.AddSingleton<DeploymentStateStore>();
        services.AddExtension<IDeploymentTarget, InMemoryTarget>("deployments.target");
        services.AddExtension<IDeploymentTarget, LocalProcessTarget>("deployments.target");
        services.AddSingleton<TargetRegistry>();
        services.AddSingleton<DeploymentRunner>();
        services.AddSingleton<DeploymentExecutor>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DeploymentExecutor>());
        services.AddSingleton<IResourceProvider, DeploymentsResourceProvider>();
        services.AddSingleton<IEventStreamResolver, DeploymentsEventStreams>();
        services.AddSingleton<IResourceHandler<EnvironmentProperties>, EnvironmentHandler>();
        services.AddSingleton<IResourceHandler<DeploymentProperties>, DeploymentHandler>();
        services.AddSingleton<IResourceHandler<TargetProjection>, TargetHandler>();
    }
}
