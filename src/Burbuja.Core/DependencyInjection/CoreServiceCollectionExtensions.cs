using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Channel;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Scheduling;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Arm;
using Burbuja.Core.Arm.Rps;
using Burbuja.Core.Clock;
using Burbuja.Core.Diagnostics;
using Burbuja.Core.Host;
using Burbuja.Core.InMemory;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Burbuja.Core.Policy;
using Burbuja.Core.Process;
using Burbuja.Core.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddBurbujaCore(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<IBurbujaPlugin>? staticPlugins = null)
    {
        AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromMilliseconds(250));
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, TimeProviderClock>();
        BindAndValidate<KernelOptions>(services, configuration, KernelOptions.Section)
            .PostConfigure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.NodeId))
                    o.NodeId = KernelOptions.FallbackNodeId();
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Role), "Kernel:Role is required.");
        BindAndValidate<AuthOptions>(services, configuration, AuthOptions.Section);
        BindAndValidate<PolicyOptions>(services, configuration, PolicyOptions.Section);
        BindAndValidate<RuntimeOptions>(services, configuration, RuntimeOptions.Section);
        BindAndValidate<ArmOptions>(services, configuration, ArmOptions.Section);
        BindAndValidate<ArmThrottleOptions>(services, configuration, ArmThrottleOptions.Section);
        BindAndValidate<BudgetOptions>(services, configuration, BudgetOptions.Section);
        BindAndValidate<LlmOptions>(services, configuration, LlmOptions.Section);
        BindAndValidate<WorkspaceOptions>(services, configuration, WorkspaceOptions.Section);

        services.AddSingleton<IRedactor, CaptureGroupRedactor>();
        services.AddSingleton<IExecutionGrantAuthority, ExecutionGrantAuthority>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IShellCommandParser, ShellCommandParser>();
        services.AddSingleton<ICache, InMemoryCache>();
        services.AddSingleton(typeof(IDecisionCache<>), typeof(InMemoryDecisionCache<>));
        services.AddSingleton<BusFaultInjector>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IEventLog>(sp => new InMemoryEventLog(
            sp.GetRequiredService<IRedactor>(),
            sp.GetRequiredService<IClock>(),
            tailerCapacity: 256,
            spill: sp.GetRequiredService<IArtifactStore>(),
            maxPayloadBytes: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RuntimeOptions>>().Value.MaxEventPayloadBytes));
        services.AddSingleton<InMemoryOutbox>();
        services.AddSingleton<IOutbox>(sp => sp.GetRequiredService<InMemoryOutbox>());
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutbox>());
        services.AddSingleton<ILeaseStore, InMemoryLeaseStore>();
        services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
        // A4: dimension-agnostic limits over the resource store. Registered as a singleton like every other
        // Core service, so a consumer resolves the interface and never the implementation — the AKOS RP
        // books committed capital and losses through the same seam the kernel's own budgets use.
        services.AddSingleton<Burbuja.Core.Abstractions.Limits.ILimitLedger, Burbuja.Core.Limits.DurableLimitLedger>();

        // A6/A3: the scope state machine. One instance serves both seams — the store a caller writes
        // through and the resolver policy reads through — so a halt is visible to the gate the moment the
        // write returns rather than through a cache that could disagree with the resource.
        services.AddSingleton<Burbuja.Core.Scopes.DurableScopeStore>();
        services.AddSingleton<Burbuja.Core.Abstractions.Scopes.IScopeStateStore>(
            sp => sp.GetRequiredService<Burbuja.Core.Scopes.DurableScopeStore>());
        services.AddSingleton<Burbuja.Core.Abstractions.Scopes.IScopeStateResolver>(
            sp => sp.GetRequiredService<Burbuja.Core.Scopes.DurableScopeStore>());
        services.AddSingleton<IApprovalService, InMemoryApprovalService>();
        services.AddSingleton<IArtifactStore>(sp => new LocalFileArtifactStore(
            Path.Combine(Path.GetTempPath(), "burbuja-artifacts"),
            sp.GetRequiredService<IRedactor>()));
        services.AddSingleton<InMemoryScheduler>();
        services.AddSingleton<IScheduler>(sp => sp.GetRequiredService<InMemoryScheduler>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<InMemoryScheduler>());
        services.AddSingleton<Outbox.OutboxRelayService>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<Outbox.OutboxRelayService>());
        services.AddSingleton<Recovery.RecoverySweeper>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<Recovery.RecoverySweeper>());

        // M7.1: the durable scheduler. Registered in the same place and the same shape as the
        // sweeper above — both are control-plane loops that read the store and act on what is due.
        services.AddSingleton<Scheduling.DurableScheduleRunner>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<Scheduling.DurableScheduleRunner>());

        services.AddSingleton<IResourceStore, InMemoryResourceStore>();
        services.AddSingleton<IOperationTracker, InMemoryOperationTracker>();
        services.AddSingleton<InMemoryIdempotencyStore>();
        services.AddSingleton<IIdempotencyStore>(sp => sp.GetRequiredService<InMemoryIdempotencyStore>());
        services.AddSingleton<InMemoryArmDirectory>();
        services.AddSingleton<IArmDirectory>(sp => sp.GetRequiredService<InMemoryArmDirectory>());
        // N4: rebuild tenancy from the store before any host-added seed service runs.
        services.AddSingleton<ArmDirectoryRehydrator>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<ArmDirectoryRehydrator>());
        services.AddSingleton(AuthorizationEvaluator.Create);
        services.AddSingleton<IAuthorizationEvaluator>(sp => sp.GetRequiredService<AuthorizationEvaluator>());
        // M11 S1: the rule that decides who may decide a card. Registered as a contract because there is more
        // than one door onto an approval — ApprovalHandler and the Agents RP's job-approvals projection.
        // NOTE: the decider takes NO routing table, and that is deliberate — see ApprovalDecider's own note.
        // The table's build constructs the resource handlers, so a handler that asked for the table would be a
        // cycle; every AKOS fact in the tree hung on the first version of this line.
        services.AddSingleton<IApprovalDecider>(sp => new ApprovalDecider(sp.GetRequiredService<AuthorizationEvaluator>()));

        // N9: in-process IArmClient that runs the real pipeline (caller supplies x-api-key).
        services.TryAddSingleton<IArmClient>(sp =>
            new ArmPipelineClient(sp.GetRequiredService<IServiceScopeFactory>(), ArmConstants.ApiVersion));

        services.AddSingleton<IResourceProvider, ResourcesProvider>();
        services.AddSingleton<IResourceProvider, AuthorizationProvider>();
        services.AddSingleton<ActivityHandler>();
        services.AddKeyedSingleton<IResourceHandler<ArmActivityRow>>(
            ArmRoutingTable.TypeKey(ArmConstants.ResourcesNamespace, "activity"),
            (sp, _) => sp.GetRequiredService<ActivityHandler>());
        services.AddSingleton<IResourceHandler<ArmActivityRow>>(sp => sp.GetRequiredService<ActivityHandler>());
        services.AddResourceType<SubscriptionProperties, SubscriptionHandler>(ArmConstants.ResourcesNamespace, "subscriptions");
        services.AddResourceType<ResourceGroupProperties, ResourceGroupHandler>(ArmConstants.ResourcesNamespace, "resourceGroups");
        services.AddResourceType<ProviderProperties, ProviderHandler>(ArmConstants.ResourcesNamespace, "providers");
        services.AddResourceType<OperationProperties, OperationHandler>(ArmConstants.ResourcesNamespace, "operations");
        services.AddResourceType<ApprovalProperties, ApprovalHandler>(ArmConstants.ResourcesNamespace, "approvals");
        services.AddResourceType<RoleDefinitionProperties, RoleDefinitionHandler>(ArmConstants.AuthorizationNamespace, "roleDefinitions");
        services.AddResourceType<RoleAssignmentProperties, RoleAssignmentHandler>(ArmConstants.AuthorizationNamespace, "roleAssignments");
        services.AddResourceType<LockProperties, LockHandler>(ArmConstants.AuthorizationNamespace, "locks");

        // K2: the activity ring. The PIPELINE writes to it, not a middleware — see ArmPipeline.Record for
        // the fact that says why: a wrapping middleware's finally runs before a throw becomes a 500, so it
        // recorded a request that failed as 200.
        services.AddSingleton<IArmActivityLog>(sp =>
            new InMemoryArmActivityLog(sp.GetService<IOptions<RuntimeOptions>>()?.Value.ActivityRingSize ?? InMemoryArmActivityLog.DefaultCapacity));
        services.AddSingleton<IArmMiddleware, RequestIdentityMiddleware>();
        services.AddSingleton<IArmMiddleware, BodyBufferingMiddleware>();
        services.AddSingleton<IArmMiddleware, AuthenticationMiddleware>();
        services.AddSingleton<IArmMiddleware, ResourceIdParseMiddleware>();
        services.AddSingleton<IArmMiddleware, ApiVersionMiddleware>();
        services.AddSingleton<IArmMiddleware, TenancyMiddleware>();
        services.AddSingleton<IArmMiddleware, AuthorizationMiddleware>();
        services.AddSingleton<IArmMiddleware, LockMiddleware>();
        services.AddSingleton<IArmMiddleware, ThrottlingMiddleware>();
        services.AddSingleton<IArmMiddleware, IdempotencyMiddleware>();
        services.AddSingleton<IArmMiddleware, ConcurrencyMiddleware>();
        services.AddSingleton<IArmMiddleware, ValidationMiddleware>();
        services.AddSingleton<IArmMiddleware, DispatchMiddleware>();
        services.AddSingleton<IArmMiddleware, AsyncOperationMiddleware>();
        services.AddSingleton<ArmPipeline>();

        services.AddSingleton<IPolicyClassifier, FsWriteClassifier>();
        services.AddSingleton<IPolicyClassifier, FsReadClassifier>();
        services.AddSingleton<IPolicyClassifier, HttpClassifier>();

        // A5/A7: the transition catalog, built from whatever contributes to it. Constructed HERE so a
        // duplicate transition name fails BOOT rather than the first request that needs it — which is the
        // whole reason the catalog validates in its constructor. A host with no contributor gets an empty
        // catalog, which is a valid state: an action request naming a transition nobody defines cannot be
        // judged, and the classifier says so rather than guessing.
        services.AddSingleton(sp => new Burbuja.Core.Abstractions.Policy.TransitionCatalog(
            sp.GetServices<Burbuja.Core.Abstractions.Policy.ITransitionContributor>()));

        services.AddSingleton<IPolicyPipeline<ActionPolicySubject>, PolicyEngine>();

        var kernelOpts = new KernelOptions();
        configuration.GetSection(KernelOptions.Section).Bind(kernelOpts);
        if (string.IsNullOrWhiteSpace(kernelOpts.NodeId))
            kernelOpts.NodeId = KernelOptions.FallbackNodeId();
        var registry = new ExtensionPointRegistry();
        registry.Declare<IPolicyRule<SegmentPolicySubject>>("policy.rules", ExtensionCardinality.OneOrMore);
        services.AddSingleton<IExtensionPointRegistry>(registry);
        var pluginLogFactory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Information));
        // Track 3a: the per-plugin health registry is created before Boot so the supervised hosted
        // services and the one-check-per-plugin registrations below share one instance.
        var pluginHealth = new PluginHealthRegistry();
        var pluginHost = new PluginHost(
            configuration,
            Microsoft.Extensions.Options.Options.Create(kernelOpts),
            pluginLogFactory.CreateLogger<PluginHost>(),
            registry,
            pluginHealth);
        pluginHost.Boot(services, staticPlugins);
        services.AddSingleton(pluginHost);
        services.AddSingleton(pluginHealth);

        // M7.1d: the process-wide host-facts snapshot. A platform provider plugin (HostFacts.MacOs)
        // collects and publishes; whoever reads facts (notifier, portal, health) reads `Latest`.
        // Registered by the kernel rather than by the provider plugin so the holder exists — and a
        // consumer can be constructed — even when no provider plugin is staged for this host.
        services.AddSingleton<IHostFactsSnapshot, HostFactsSnapshot>();

        // M7.1e: notices fan out to whatever channels are registered, and the publisher turns the
        // provider's readings into a monitor-lane event plus edge-triggered threshold notices.
        services.AddSingleton<INotifier, Channel.ChannelNotifier>();
        services.AddSingleton<Host.HostFactsPublisher>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<Host.HostFactsPublisher>());
        services.AddSingleton(KernelHostInfo.Capture(kernelOpts));
        services.AddSingleton<ArmRoutingTable>(ArmRoutingTableBuilder.Build);
        services.AddSingleton<ArmRoutingTableStartup>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<ArmRoutingTableStartup>());

        // Track 3a: the old single always-Healthy `plugins` aggregate is gone. Each loaded plugin
        // gets its own check (`plugin:<Id>`), so /health/plugins can say WHICH plugin is degraded and
        // a caller can probe one plugin with a `plugin:<Id>` tag filter. The process-level checks
        // (`live`, `ready`) are untouched: compose's healthcheck and the E2E readiness poll depend on
        // their exact answers, and plugin health is not a process-readiness question.
        var healthChecks = services.AddHealthChecks()
            .AddCheck("live", () => HealthCheckResult.Healthy("live"), tags: ["live"])
            .AddCheck("ready", () => HealthCheckResult.Healthy("ready"), tags: ["ready"]);
        foreach (var plugin in pluginHost.Loaded)
        {
            var id = plugin.Manifest.Id;
            // A factory registration (not AddCheck<T>): the check needs the plugin id and resolves its
            // contributors from the per-check DI scope. This package's AddCheck has no
            // Func<IServiceProvider, IHealthCheck> overload, so the HealthCheckRegistration is built
            // directly.
            healthChecks.Add(new HealthCheckRegistration(
                $"plugin:{id}",
                sp => new PluginHealthCheck(pluginHealth, id, sp),
                failureStatus: null,
                tags: ["plugins", $"plugin:{id}"]));
        }

        return services;
    }

    public static IServiceCollection AddResourceProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IResourceProvider
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResourceProvider, TProvider>());
        return services;
    }

    public static IServiceCollection AddResourceType<TProps, THandler>(this IServiceCollection services, string ns, ResourceTypeDescriptor descriptor)
        where TProps : class
        where THandler : class, IResourceHandler<TProps> =>
        services.AddResourceType<TProps, THandler>(ns, descriptor.Type);

    public static IServiceCollection AddResourceType<TProps, THandler>(this IServiceCollection services, string ns, string type)
        where TProps : class
        where THandler : class, IResourceHandler<TProps>
    {
        var key = ArmRoutingTable.TypeKey(ns, type);
        services.TryAddSingleton<THandler>();
        services.AddKeyedSingleton<IResourceHandler<TProps>>(key, (sp, _) => sp.GetRequiredService<THandler>());
        services.TryAddSingleton<IResourceHandler<TProps>>(sp => sp.GetRequiredService<THandler>());
        return services;
    }

    private static OptionsBuilder<T> BindAndValidate<T>(IServiceCollection services, IConfiguration configuration, string section)
        where T : class =>
        services.AddOptions<T>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
}

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddBurbujaSources(this IConfigurationBuilder builder, string[]? args = null)
    {
        builder.AddEnvironmentVariables("BURBUJA_");
        builder.Add(new EnvAliasConfigurationSource());
        if (args is { Length: > 0 })
            builder.AddCommandLine(args, new Dictionary<string, string> { ["--role"] = "Kernel:Role" });
        return builder;
    }
}
