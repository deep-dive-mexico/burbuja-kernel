using System.Text.Json;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Model;
using Burbuja.Agents.Handlers;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;

namespace Burbuja.Agents;

public sealed class AgentsResourceProvider : IResourceProvider
{
    public string Namespace => AgentsConstants.Namespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; } =
    [
        DescriptorBuilder.For("workspaces")
            .Properties<WorkspaceProperties>()
            .Schema(LoadSchema("workspace.schema.json"))
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Patch
                | ResourceCapabilities.Delete
                | ResourceCapabilities.ListByResourceGroup
                | ResourceCapabilities.ListBySubscription
                | ResourceCapabilities.Actions)
            // W1: `initialize` provisions the workspace's defaults through an operation; `explainPolicy` is the
            // policy question. The type is AsyncPut because a PUT that asks for provisioning answers 202 — the
            // descriptor says so rather than leaving a client to discover it from a status code.
            .AsyncPut()
            .Actions(
                new ResourceActionDescriptor("explainPolicy", false),
                // M11 U12 — `initialize` is NOT idempotent in the front-door sense, and G26 is how that was
                // found. The OPERATION is idempotent (a re-initialize reconciles and the generation does not
                // move — W1's acceptance), but the RESPONSE must not be replayed: the front door keys on
                // principal|method|resource|api-version|bodyHash, so a reader who pressed the button twice sent
                // two identical requests and the SECOND was answered from the first's 24-hour cache. The fact
                // asserting "a second run reconciles" was therefore asserting a run that never happened, and
                // the page showed the provisional answer forever. Same shape as AKOS `advance`.
                new ResourceActionDescriptor(WorkspaceHandler.Initialize, false, IsDataAction: true, Idempotent: false))
            .Children("jobs", "schedules")
            .DataPlane(new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents))
            .Build(),
        DescriptorBuilder.For("workspaces/jobs")
            .Properties<JobProperties>()
            .Schema(LoadSchema("job.schema.json"))
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Delete
                | ResourceCapabilities.ListByResourceGroup
                | ResourceCapabilities.Actions)
            .AsyncPut()
            .Actions(
                new ResourceActionDescriptor("accept", false),
                new ResourceActionDescriptor("steer", false),
                new ResourceActionDescriptor("cancel", false),
                new ResourceActionDescriptor("extendBudget", false))
            .Children("runs", "approvals")
            .DataPlane(
                new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents),
                new DataPlaneRouteDescriptor("artifacts", DataPlaneKind.Download))
            .Build(),
        DescriptorBuilder.For("workspaces/jobs/runs")
            .Properties<RunProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.ListByResourceGroup)
            .DataPlane(new DataPlaneRouteDescriptor("transcript", DataPlaneKind.Download))
            .Build(),
        DescriptorBuilder.For("workspaces/jobs/approvals")
            .Properties<ApprovalProjection>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.ListByResourceGroup | ResourceCapabilities.Actions)
            .Actions(
                new ResourceActionDescriptor("approve", false),
                new ResourceActionDescriptor("deny", false),
                new ResourceActionDescriptor("answer", false))
            .Build(),
        DescriptorBuilder.For("workspaces/schedules")
            .Properties<ScheduleProperties>()
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Patch
                | ResourceCapabilities.Delete
                | ResourceCapabilities.ListByResourceGroup
                | ResourceCapabilities.Actions)
            .Actions(
                new ResourceActionDescriptor("run", false),
                new ResourceActionDescriptor("pause", false))
            .Build(),
        DescriptorBuilder.For("nodes")
            .Properties<NodeProjection>()
            .Tracked(false)
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List | ResourceCapabilities.Actions)
            .Actions(new ResourceActionDescriptor("kill", false))
            .Build(),
        // M4.3 stateful runtime: agents are sync-PUT (creating one never runs a turn); a session
        // outlives jobs and is append-only; contexts are bounded, redacted cards.
        DescriptorBuilder.For("agents")
            .Properties<AgentProperties>()
            .Schema(SchemaBuilder.Object())
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Delete
                | ResourceCapabilities.ListByResourceGroup
                | ResourceCapabilities.List
                | ResourceCapabilities.Actions)
            .Actions(
                new ResourceActionDescriptor("send", true),
                new ResourceActionDescriptor("interrupt", true),
                new ResourceActionDescriptor("fork", true),
                new ResourceActionDescriptor("resume", true))
            .Children("sessions", "contexts")
            .RbacActions(AgentActions.Tools)
            .DataPlane(
                new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents),
                new DataPlaneRouteDescriptor("transcript", DataPlaneKind.Download))
            .Build(),
        DescriptorBuilder.For("agents/sessions")
            .Properties<SessionProperties>()
            .Schema(SchemaBuilder.Object())
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.ListByResourceGroup | ResourceCapabilities.List)
            .DataPlane(
                new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents),
                new DataPlaneRouteDescriptor("transcript", DataPlaneKind.Download))
            .Build(),
        DescriptorBuilder.For("teams")
            .Properties<TeamProperties>()
            .Schema(SchemaBuilder.Object())
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Delete
                | ResourceCapabilities.List
                | ResourceCapabilities.ListByResourceGroup
                | ResourceCapabilities.Actions)
            .Actions(
                new ResourceActionDescriptor("spawnMember", false),
                new ResourceActionDescriptor("waitForChange", false),
                new ResourceActionDescriptor("fold", false))
            .Children("tasks", "messages")
            .Build(),
        DescriptorBuilder.For("teams/tasks")
            .Properties<TeamTaskProperties>()
            .Schema(SchemaBuilder.Object())
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Patch
                | ResourceCapabilities.Delete
                | ResourceCapabilities.List
                | ResourceCapabilities.ListByResourceGroup)
            .Build(),
        DescriptorBuilder.For("teams/messages")
            .Properties<TeamMessageProperties>()
            .Schema(SchemaBuilder.Object())
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.List | ResourceCapabilities.ListByResourceGroup)
            .Build(),
        DescriptorBuilder.For("agents/contexts")
            .Properties<ContextCardProperties>()
            .Schema(SchemaBuilder.Object())
            .Capabilities(
                ResourceCapabilities.Get
                | ResourceCapabilities.Put
                | ResourceCapabilities.Delete
                | ResourceCapabilities.ListByResourceGroup
                | ResourceCapabilities.List)
            .Build(),
        // M5.1: the durable budget ledger, one read-only resource per UTC day. The runtime writes it
        // through IResourceStore, so every write verb is a 405.
        DescriptorBuilder.For("ledgers")
            .Properties<LedgerDayProjection>()
            .Tracked(false)
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List)
            .Build(),
    ];

    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;

    public static JsonElement LoadSchema(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Schemas", fileName);
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, fileName);
        string json;
        if (File.Exists(path))
            json = File.ReadAllText(path);
        else
        {
            var asm = typeof(AgentsResourceProvider).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return SchemaBuilder.Object();
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        return SchemaBuilder.Parse(json);
    }
}
