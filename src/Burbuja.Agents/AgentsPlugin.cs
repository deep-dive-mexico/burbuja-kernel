using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Arbiter;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Nodes;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Registry;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Abstractions.Workspace;
using Burbuja.Agents.Arm;
using Burbuja.Agents.Handlers;
using Burbuja.Agents.Model;
using Burbuja.Agents.Roles;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Recovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Burbuja.Agents;

public sealed class AgentsPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Burbuja.Agents",
        "Burbuja Agents",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Agents.dll",
        typeof(AgentsPlugin).FullName!,
        PluginLoadKind.Static,
        [AgentsConstants.Namespace],
        [],
        [],
        ["all", "control-plane", "worker"],
        AgentsOptions.Section,
        false,
        [],
        200);

    /// <summary>
    /// The Agents RP owns these points; Core may not reference Agents, so the declarations live here.
    /// </summary>
    public void DeclareExtensionPoints(IExtensionPointRegistry registry)
    {
        registry.Declare<ILlmProvider>("llm.provider", ExtensionCardinality.OneOrMore, PluginCapability.Network);
        registry.Declare<IAgentTool>("agents.tool", ExtensionCardinality.OneOrMore);
        registry.Declare<IGrayZoneArbiter>("agents.arbiter", ExtensionCardinality.Optional);
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<AgentsOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(AgentsOptions.Section).Bind(o));
        services.AddSingleton<IRoleContributor, AgentsRoles>();
        services.AddSingleton<CancellationRegistry>();
        services.AddSingleton<SteeringInbox>();
        services.AddSingleton<JobStateStore>();
        services.AddSingleton<SessionLog>();
        services.AddSingleton<AgentActivationManager>();
        services.AddSingleton<TeamBoard>();
        services.AddSingleton<AgentSessionStore>();
        services.AddSingleton<AgentTurnRunner>();
        services.AddSingleton<AgentActivationWorker>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentActivationWorker>());
        // W1: the workspace lifecycle lane. Registered as a singleton and forwarded to IHostedService, the
        // same shape the activation worker uses, so a fact can drive the initializer without the bus.
        services.AddSingleton<Burbuja.Agents.Runtime.WorkspaceInitializer>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<Burbuja.Agents.Runtime.WorkspaceInitializer>());
        // M5.1: cost is real for every model a contributed price book knows (the Sonnet default is
        // still the fallback book), and the ledger rides IResourceStore so Store.Sqlite makes it
        // durable without a new table or a cross-plugin replace.
        services.AddSingleton<ICostCalculator>(sp =>
            new CompositeCostCalculator(sp.GetServices<IModelPricebook>(), sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IBudgetLedger>(sp =>
            new DurableBudgetLedger(sp.GetRequiredService<IResourceStore>(), sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IRepoRegistry>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentsOptions>>().Value;
            var path = opts.ReposFile;
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(AppContext.BaseDirectory, "config", "repos.yaml");
            return new YamlRepoRegistry(path);
        });
        services.AddSingleton<IWorkspaceProvider, InMemoryWorkspaceProvider>();
        // NOT registered as an ILlmProvider, deliberately. `NullLlmProvider` is the router's own fallback
        // (`installed.LastOrDefault() ?? new NullLlmProvider()`), and putting it in the provider LIST made
        // "which llm.providers are installed" answer "null": a console reading that list to decide whether a
        // host can run an agent at all would have called a host with no model AVAILABLE. Measured while
        // building the AKOS roster console, whose availability column is computed from exactly this list.
        services.AddSingleton<LlmProviderRouter>();
        services.AddSingleton<INodeDirectory>(sp =>
        {
            var n = sp.GetRequiredService<IOptions<AgentsOptions>>().Value.NodeCapacity;
            return new NodeRegistry(n);
        });
        services.AddSingleton<NarrowingArbiter>(sp => new NarrowingArbiter(
            sp.GetService<IGrayZoneArbiter>(),
            sp.GetRequiredService<Burbuja.Core.Abstractions.Store.IDecisionCache<ArbiterDecision>>(),
            sp.GetRequiredService<Burbuja.Core.Abstractions.Events.IEventLog>()));
        services.AddSingleton<ToolGate>();
        services.AddSingleton<ToolBatchExecutor>();
        services.AddSingleton<RunGroupJoiner>();
        services.AddSingleton<IIntakeStrategy, HeuristicIntakeStrategy>();
        services.AddSingleton<IntakeService>(sp => new IntakeService(
            sp.GetRequiredService<LlmProviderRouter>(),
            sp.GetRequiredService<IIntakeStrategy>(),
            sp.GetService<IRepoRegistry>()));
        services.AddSingleton<AgentRunner>();
        services.AddSingleton<ScenarioPlanProvider>(_ => new ScenarioPlanProvider());
        services.AddSingleton<IOrchestrationStrategy, SingleWorkerStrategy>();
        services.AddSingleton<IOrchestrationStrategy, PlanFanOutMergeStrategy>();
        services.AddSingleton<StrategyRegistry>();
        services.AddSingleton<JobOrchestrator>();
        services.AddSingleton<AgentExecutor>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentExecutor>());
        services.AddSingleton<IStreamRecoveryHandler, JobRecoveryHandler>();
        services.AddSingleton<JobToolJournal>();
        services.AddSingleton<IResourceProvider, AgentsResourceProvider>();
        services.AddSingleton<IEventStreamResolver, AgentsEventStreams>();
        services.AddSingleton<IDataPlaneHandler, AgentsArtifactsHandler>();
        services.AddSingleton<IResourceHandler<AgentProperties>, AgentHandler>();
        services.AddSingleton<IResourceHandler<TeamProperties>, TeamHandler>();
        services.AddSingleton<IResourceHandler<TeamTaskProperties>, TeamTaskHandler>();
        services.AddSingleton<IResourceHandler<TeamMessageProperties>, TeamMessageHandler>();
        services.AddSingleton<IResourceHandler<SessionProperties>, SessionHandler>();
        services.AddSingleton<IResourceHandler<ContextCardProperties>, ContextHandler>();
        services.AddSingleton<IResourceHandler<WorkspaceProperties>, WorkspaceHandler>();
        services.AddSingleton<IResourceHandler<JobProperties>, JobHandler>();
        services.AddSingleton<IResourceHandler<RunProperties>, RunHandler>();
        services.AddSingleton<IResourceHandler<ApprovalProjection>, JobApprovalHandler>();
        services.AddSingleton<IResourceHandler<ScheduleProperties>, ScheduleHandler>();
        services.AddSingleton<IResourceHandler<NodeProjection>, NodeHandler>();
        services.AddSingleton<IResourceHandler<LedgerDayProjection>, LedgerHandler>();
    }
}
