using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Handlers;

public sealed class WorkspaceHandler(
    IResourceStore store,
    IClock clock,
    IPolicyPipeline<ActionPolicySubject> policy,
    IOperationTracker ops,
    IOutbox outbox,
    IEventLog events) : EnvelopeHandler<WorkspaceProperties>(store, clock)
{
    /// <summary>
    /// The Compute RP's namespace, spelled here rather than referenced: the dividing rule keeps
    /// <c>Burbuja.Agents</c> from depending on <c>Burbuja.Compute</c>, and the binding is an ARM id either
    /// way — the same discipline the AKOS plugin follows when it drives agents through the front door.
    /// </summary>
    public const string ComputeNamespace = "Burbuja.Compute";

    /// <summary>W1: the verb that provisions a workspace's defaults, and the one the PUT defers to.</summary>
    public const string Initialize = "initialize";

    /// <summary>The lifecycle values a workspace can be in. A partition, so a reader can enumerate it.</summary>
    public static class Lifecycle
    {
        public const string Provisioning = "Provisioning";
        public const string Ready = "Ready";
        public const string Failed = "Failed";
    }

    public const string ProvisioningEvent = "agents.workspace.provisioning";
    public const string ReadyEvent = "agents.workspace.ready";
    public const string FailedEvent = "agents.workspace.initialize.failed";

    /// <summary>
    /// W1: the compute workspace a name binds to when a caller does not name one — the same name, in the
    /// same resource group, under the Compute namespace. Deriving it rather than inventing a suffix keeps
    /// "where does this workspace run" answerable by reading the two ids side by side.
    /// </summary>
    public static string DefaultComputeId(ResourceId id)
    {
        var ws = id.NameSegments.LastOrDefault() ?? "workspace";
        var root = id.ResourceGroup is { Length: > 0 } rg
            ? $"/subscriptions/{id.SubscriptionId}/resourceGroups/{rg}"
            : $"/subscriptions/{id.SubscriptionId}";
        return $"{root}/providers/{ComputeNamespace}/workspaces/{ws}";
    }

    /// <summary>How many job rows the hub projection reads. Past this the counts are a floor and
    /// <c>Truncated</c> says so, which is a bound a reader can see rather than a silent cap.</summary>
    public const int HubScanCeiling = 500;

    /// <summary>How many recent jobs the hub carries. The list is for deciding what to open, not for
    /// paging — the job list is where paging belongs.</summary>
    public const int HubRecentLimit = 10;

    /// <summary>
    /// The statuses, grouped, as data rather than as a switch with a default arm.
    ///
    /// <para><b>A partition, and a fact reads it.</b> Six groups cover the ten
    /// <see cref="JobExecutionStatus"/> values, so a status added to the enum without deciding which group
    /// it belongs to fails a test instead of disappearing into an "other" bucket nobody looks at — the same
    /// shape as the output statuses and the integrity actions. The names are what the hub's <c>byStatus</c>
    /// map is keyed by, so the portal and the CLI render the same words.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<JobExecutionStatus>> JobGroups =
        new Dictionary<string, IReadOnlySet<JobExecutionStatus>>(StringComparer.Ordinal)
        {
            ["queued"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.Queued },
            ["paused"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.Paused },
            ["active"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.Starting, JobExecutionStatus.Running },
            ["parked"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.AwaitingApproval },
            // M10 H3. `blocked` is deliberately NOT folded into `parked`: parked means "a human has a decision
            // to make", and a job at the day's hard stop has none — nobody can approve money. Counting the two
            // together would file a job nobody can unblock under the group an operator scans for work waiting on
            // them, which is how a queue starts lying about itself.
            ["blocked"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.Blocked },
            ["succeeded"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.Succeeded },
            ["failed"] = new HashSet<JobExecutionStatus>
            {
                JobExecutionStatus.Failed, JobExecutionStatus.BudgetExceeded, JobExecutionStatus.TurnsExceeded,
            },
            ["cancelled"] = new HashSet<JobExecutionStatus> { JobExecutionStatus.Cancelled },
        };

    protected override string TypeName => $"{AgentsConstants.Namespace}/workspaces";

    /// <summary>
    /// Read one workspace, with K3's hub filled in.
    ///
    /// <para><b>Why the read and not a stored copy.</b> Every number in the hub is derived from resources
    /// that already exist — the jobs and the schedules — and a stored second copy is a thing that can
    /// disagree with them. The cost is one bounded job scan per single-resource read, which is why the LIST
    /// does not fill it (see <see cref="WorkspaceProperties.Hub"/>).</para>
    /// </summary>
    public override async ValueTask<ResourceEnvelope<WorkspaceProperties>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var envelope = await base.GetAsync(req, ct).ConfigureAwait(false);
        if (envelope is null)
            return null;

        // Mutating the mapped instance is safe: `Map` deserialises a FRESH object per call, so this is not
        // shared state — it is the copy this read is about to return.
        envelope.Properties.Hub = await HubAsync(req, envelope.Properties, ct).ConfigureAwait(false);
        return envelope;
    }

    private async ValueTask<WorkspaceHub> HubAsync(ResourceRequest req, WorkspaceProperties workspace, CancellationToken ct)
    {
        var hub = new WorkspaceHub
        {
            DefaultMaxUsd = workspace.DefaultMaxUsd,
            GeneratedAt = Clock.UtcNow,
        };

        // The workspace's own jobs: the store query walks the workspace's path, which is the same filter a
        // reader listing `workspaces/{ws}/jobs` gets.
        var jobs = await Store.ListAsync(
            new ResourceStoreQuery(
                null, null, AgentsConstants.Namespace, $"{AgentsConstants.Namespace}/workspaces/jobs",
                HubScanCeiling, null, ResourceIdentity(req.Id).Canonical, null),
            ct).ConfigureAwait(false);

        hub.Scanned = jobs.Value.Count;
        hub.Truncated = jobs.Value.Count >= HubScanCeiling;
        hub.Jobs = 0;

        foreach (var group in JobGroups.Keys)
            hub.ByStatus[group] = 0;

        var rows = new List<(WorkspaceHubJob Row, DateTimeOffset At)>();
        foreach (var job in jobs.Value)
        {
            var properties = job.Properties.Deserialize<JobProperties>(Burbuja.Core.Abstractions.Json.JsonWire.Options);
            if (properties is null)
                continue;

            hub.Jobs += 1;
            var group = JobGroups.FirstOrDefault(g => g.Value.Contains(properties.Status));
            if (group.Key is { Length: > 0 })
                hub.ByStatus[group.Key] += 1;
            hub.SpendUsd += properties.Cost?.TotalUsd ?? 0m;

            rows.Add((
                new WorkspaceHubJob
                {
                    Name = job.Name,
                    Status = properties.Status.ToString(),
                    Title = properties.Spec?.Title ?? "",
                    Usd = properties.Cost?.TotalUsd ?? 0m,
                    UpdatedAt = job.SystemData.LastModifiedAt ?? job.SystemData.CreatedAt,
                    PendingApprovalId = properties.PendingApprovalId,
                },
                job.SystemData.LastModifiedAt ?? job.SystemData.CreatedAt ?? DateTimeOffset.MinValue));
        }

        hub.Recent = [.. rows.OrderByDescending(r => r.At).Take(HubRecentLimit).Select(r => r.Row)];

        // The schedules: the workspace's own children, and the soonest window among the live ones.
        var schedules = await Store.ListAsync(
            new ResourceStoreQuery(
                null, null, AgentsConstants.Namespace, $"{AgentsConstants.Namespace}/workspaces/schedules",
                HubScanCeiling, null, ResourceIdentity(req.Id).Canonical, null),
            ct).ConfigureAwait(false);

        foreach (var schedule in schedules.Value)
        {
            var properties = schedule.Properties.Deserialize<ScheduleProperties>(Burbuja.Core.Abstractions.Json.JsonWire.Options);
            if (properties is null)
                continue;

            hub.Schedules += 1;
            if (properties.Paused)
            {
                hub.PausedSchedules += 1;
                continue;
            }

            if (properties.NextRunAt is { } next && (hub.NextScheduleRun is null || next < hub.NextScheduleRun))
                hub.NextScheduleRun = next;
        }

        return hub;
    }

    /// <summary>
    /// W1: an ordinary resource write — 200/201, now — unless the body asks for provisioning.
    ///
    /// <para><b>The two triggers, and why nothing else provisions.</b> <c>properties.initialize: true</c> is a
    /// caller saying "set this workspace up"; a <c>computeWorkspaceId</c> that names a compute workspace this
    /// host does not have is the same request implied by the binding. Everything else stays the write every
    /// existing caller depends on, because turning an ordinary PUT into a 202 would break pomnibot's client
    /// and the E2E suites — the plan's own constraint, and the reason the flag exists.</para>
    /// </summary>
    public override async ValueTask<PutOutcome<WorkspaceProperties>> PutAsync(ResourceRequest req, WorkspaceProperties desired, CancellationToken ct)
    {
        desired.Revision = Math.Max(1, desired.Revision);
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        var existing = existingRaw is null ? null : Map(existingRaw);
        if (existing is not null)
            desired.Revision += 1;

        var bindingMissing = desired.ComputeWorkspaceId is { Length: > 0 } named && !await ExistsAsync(named, ct);
        if (!desired.Initialize && !bindingMissing)
            return await base.PutAsync(req, desired, ct);

        // Provisioning: accepted now, provisioned by the worker. The binding is written before the operation
        // starts so the resource a caller polls already says what it is being bound to.
        desired.ComputeWorkspaceId ??= DefaultComputeId(req.Id);
        desired.Lifecycle = Lifecycle.Provisioning;
        desired.LifecycleError = null;
        desired.OperationId = Guid.NewGuid().ToString("N");
        desired.Generation = (existing?.Properties.Generation ?? 0) + 1;
        desired.Initialize = false;

        var stored = await base.PutAsync(req, desired, ct);
        var envelope = stored switch
        {
            PutOutcome<WorkspaceProperties>.Created created => created.Resource,
            PutOutcome<WorkspaceProperties>.Updated updated => updated.Resource,
            _ => null,
        };
        if (envelope is null)
            return stored;

        await ops.StartAsync(desired.OperationId, req.Id.SubscriptionId, req.Id.Namespace, "local", envelope.Id, null, ct);
        await events.AppendAsync(new EventAppend(
            AgentsConstants.StreamId(ResourceIdentity(req.Id).Canonical),
            null,
            ProvisioningEvent,
            "provisioning",
            new JsonObject
            {
                ["computeWorkspaceId"] = desired.ComputeWorkspaceId,
                ["operationId"] = desired.OperationId,
                ["generation"] = desired.Generation,
            },
            RenderLane.Log,
            envelope.Name,
            null,
            AgentsConstants.Namespace), ct).ConfigureAwait(false);
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicWorkspaceLifecycle,
            new JsonObject
            {
                ["workspaceKey"] = StoreKey(req.Id),
                ["computeWorkspaceId"] = desired.ComputeWorkspaceId,
                ["operationId"] = desired.OperationId,
            }), ct);

        return new PutOutcome<WorkspaceProperties>.Accepted(envelope, desired.OperationId, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// W1: the action. The same provisioning the PUT defers to, and an idempotent answer once the workspace is
    /// ready — a console's "initialize" button must be safe to press twice, and the second press must not
    /// restart the world.
    /// </summary>
    private async ValueTask<ActionOutcome> InitializeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var current = await GetAsync(req, ct);
        if (current is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(req.Id.Canonical, req.Id.Canonical));

        var properties = current.Properties;
        var compute = properties.ComputeWorkspaceId;
        if (properties.Lifecycle == Lifecycle.Ready && compute is { Length: > 0 } && await ExistsAsync(compute, ct))
        {
            return new ActionOutcome.Ok(new JsonObject
            {
                ["workspace"] = current.Id,
                ["computeWorkspaceId"] = compute,
                ["lifecycle"] = Lifecycle.Ready,
                ["generation"] = properties.Generation,
                ["reconciled"] = false,
                ["note"] = "already initialized; nothing was provisioned again. A re-initialize reconciles, and "
                           + "this workspace's binding is already in place.",
            });
        }

        var request = new ResourceRequest
        {
            Id = ResourceIdentity(req.Id),
            ApiVersion = req.ApiVersion,
            Principal = req.Principal,
            RequestId = req.RequestId,
            CorrelationId = req.CorrelationId,
            Services = req.Services,
        };
        // The instance came out of the store for this call, so mutating it is local: a caller's "initialize"
        // is exactly the PUT a caller would have sent with the flag set.
        properties.Initialize = true;
        var outcome = await PutAsync(request, properties, ct).ConfigureAwait(false);
        return outcome switch
        {
            PutOutcome<WorkspaceProperties>.Accepted accepted => new ActionOutcome.Accepted(
                accepted.OperationId,
                accepted.RetryAfter == default ? TimeSpan.FromSeconds(2) : accepted.RetryAfter,
                JsonSerializer.SerializeToNode(accepted.Resource, JsonWire.Options)),
            PutOutcome<WorkspaceProperties>.Created or PutOutcome<WorkspaceProperties>.Updated =>
                new ActionOutcome.Ok(new JsonObject { ["workspace"] = current.Id, ["lifecycle"] = properties.Lifecycle }),
            PutOutcome<WorkspaceProperties>.Rejected rejected => new ActionOutcome.Rejected(rejected.Error),
            _ => new ActionOutcome.Rejected(ArmError.Validation("The workspace could not be initialized.", [])),
        };
    }

    /// <summary>Whether the named compute workspace exists — one read through the store the Compute RP writes.</summary>
    private async ValueTask<bool> ExistsAsync(string computeWorkspaceId, CancellationToken ct)
    {
        // The key shape is the canonical id lowercased with the namespace/type segments collapsed the way
        // ResourceId.Key builds it; read through the same store every RP uses rather than through ARM, because
        // this is a question about what exists and not a command.
        var key = ResourceKey(computeWorkspaceId);
        return await Store.GetAsync(key, ct) is not null;
    }

    /// <summary>The store key of another resource from its canonical id.</summary>
    private static string ResourceKey(string canonicalId) => canonicalId.TrimEnd('/').ToLowerInvariant();

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (req.Action.Equals(Initialize, StringComparison.OrdinalIgnoreCase))
            return await InitializeAsync(req, ct);

        if (!req.Action.Equals("explainPolicy", StringComparison.OrdinalIgnoreCase))
            return await base.InvokeAsync(req, ct);
        var body = req.Body?["properties"] as JsonObject ?? req.Body ?? new JsonObject();
        var tool = body["toolName"]?.GetValue<string>() ?? "";
        var input = body["input"] as JsonNode ?? new JsonObject();
        var ws = await GetAsync(req, ct);
        var workRoot = Path.GetTempPath();
        var verdict = policy.Classify(new ActionPolicySubject(
            tool,
            input,
            new PolicyContext(
                req.Id.Canonical,
                workRoot,
                workRoot,
                PolicyProfileParser.Parse(ws?.Properties.Profile),
                "dev",
                workRoot,
                ws?.Properties.Verify ?? [],
                [],
                [])));
        return new ActionOutcome.Ok(new JsonObject
        {
            ["tier"] = verdict.Tier.ToString(),
            ["class"] = verdict.Finding.Class,
            ["reason"] = verdict.Finding.Reason,
            ["fingerprint"] = verdict.Finding.Fingerprint,
        });
    }
}
