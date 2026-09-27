using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Nodes;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Scheduling;
using Burbuja.Core.Arm;
using Microsoft.Extensions.Configuration;

namespace Burbuja.Agents.Handlers;

public sealed class RunHandler(IResourceStore store, IClock clock)
    : ChildEnvelopeHandler<RunProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/workspaces/jobs/runs";

    public override ValueTask<PutOutcome<RunProperties>> PutAsync(ResourceRequest req, RunProperties desired, CancellationToken ct) =>
        new(new PutOutcome<RunProperties>.Rejected(ArmError.MethodNotAllowed("runs are created by the worker.")));
}

public sealed class JobApprovalHandler(
    IApprovalService approvals,
    IOutbox outbox,
    IClock clock,
    /// <summary>
    /// M11 S1 — the SAME rule the core approvals door applies, because this is a second door onto the same
    /// cards. <c>Burbuja.Agents/workspaces/jobs/{job}/approvals/{id}</c> is a filtered VIEW over core
    /// approvals, and its verbs called <see cref="IApprovalService"/> directly — so a role refused
    /// <c>Burbuja.Pomnibot/*</c> (or Agents/*) could still settle a job card here, which made the exclusion
    /// decorative. A rule enforced at one door is a rule with a way around it.
    /// </summary>
    IApprovalDecider decider)
    : IResourceHandler<ApprovalProjection>
{
    public async ValueTask<ResourceEnvelope<ApprovalProjection>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var id = req.Id.NameSegments.LastOrDefault() ?? "";
        var rec = await approvals.GetAsync(id, ct);
        return rec is null ? null : Envelope(rec, JobCanonical(req.Id), clock);
    }

    public ValueTask<PutOutcome<ApprovalProjection>> PutAsync(ResourceRequest req, ApprovalProjection desired, CancellationToken ct) =>
        new(new PutOutcome<ApprovalProjection>.Rejected(ArmError.MethodNotAllowed("approvals are opened by the gate.")));

    public ValueTask<PutOutcome<ApprovalProjection>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<ApprovalProjection>.Rejected(ArmError.MethodNotAllowed("approvals are opened by the gate.")));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed("approvals cannot be deleted.")));

    public async ValueTask<PagedResult<ResourceEnvelope<ApprovalProjection>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var job = JobCanonical(req.Id);
        var page = await approvals.ListAsync(new ApprovalQuery(req.Id.SubscriptionId, "tool", job, AgentsConstants.StreamId(job), false, req.Top, req.ContinuationToken), ct);
        return new PagedResult<ResourceEnvelope<ApprovalProjection>>(
            page.Value.Select(r => Envelope(r, job, clock)).ToList(),
            page.NextLink);
    }

    public async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var id = req.Id.NameSegments.LastOrDefault() ?? "";
        var body = req.Body?["properties"] as JsonObject ?? req.Body;

        // M11 S1 — the same check as the core door, and the same actor stamp.
        if (await approvals.GetAsync(id, ct).ConfigureAwait(false) is not { } record)
            return new ActionOutcome.Rejected(ArmError.NotFound(req.Id.Canonical, id));
        if (await decider.RefuseAsync(record, req.Principal, ct).ConfigureAwait(false) is { } refusal)
            return new ActionOutcome.Rejected(refusal);

        var actor = req.Principal.PrincipalId;
        if (req.Action.Equals("approve", StringComparison.OrdinalIgnoreCase))
            await approvals.ApproveAsync(id, body?["note"]?.GetValue<string>(), null, null, ct, actor);
        else if (req.Action.Equals("deny", StringComparison.OrdinalIgnoreCase))
            await approvals.DenyAsync(id, body?["note"]?.GetValue<string>(), ct, actor);
        else if (req.Action.Equals("answer", StringComparison.OrdinalIgnoreCase))
            await approvals.AnswerAsync(id, body?["answer"]?.GetValue<string>() ?? "", ct, actor);
        else
            return new ActionOutcome.Rejected(ArmError.ActionNotSupported(req.Action));

        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicRun, new JsonObject
        {
            ["jobKey"] = JobCanonical(req.Id).ToLowerInvariant(),
        }), ct);
        return new ActionOutcome.Ok(new JsonObject { ["id"] = id });
    }

    private static string JobCanonical(ResourceId id)
    {
        var parent = id.Parent ?? id;
        return parent.FullyQualifiedType.Equals("workspaces/jobs", StringComparison.OrdinalIgnoreCase)
            ? parent.Canonical
            : parent.Parent?.Canonical ?? parent.Canonical;
    }

    private static ResourceEnvelope<ApprovalProjection> Envelope(ApprovalRecord rec, string job, IClock clock) =>
        new(
            job + "/approvals/" + rec.Id,
            rec.Id,
            $"{AgentsConstants.Namespace}/workspaces/jobs/approvals",
            "local",
            new Dictionary<string, string>(),
            new ApprovalProjection
            {
                ApprovalId = rec.Id,
                Status = rec.Outcome?.GetType().Name.ToLowerInvariant() ?? "pending",
                DecisionKey = rec.Request.DecisionKey,
                RiskClass = rec.Request.RiskClass,
                Preview = rec.Request.Preview,
            },
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
            "W/\"1\"");
}

public sealed class ScheduleHandler(
    IResourceStore store,
    IClock clock,
    IScheduler scheduler,
    IOutbox outbox) : ChildEnvelopeHandler<ScheduleProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/workspaces/schedules";

    public override async ValueTask<PutOutcome<ScheduleProperties>> PutAsync(ResourceRequest req, ScheduleProperties desired, CancellationToken ct)
    {
        var result = await base.PutAsync(req, desired, ct);
        if (!desired.Paused)
        {
            scheduler.Schedule(req.Id.Key, Clock.UtcNow.AddMinutes(1), async token =>
            {
                await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicRun, new JsonObject
                {
                    ["jobKey"] = req.Id.Parent?.Key ?? req.Id.Key,
                }), token);
            });
        }

        return result;
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (req.Action.Equals("pause", StringComparison.OrdinalIgnoreCase))
        {
            scheduler.Cancel(StoreKey(req.Id));
            return new ActionOutcome.Ok(new JsonObject { ["paused"] = true });
        }

        if (req.Action.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicRun, new JsonObject
            {
                ["jobKey"] = req.Id.Parent?.Key ?? StoreKey(req.Id),
            }), ct);
            return new ActionOutcome.Accepted(Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(1), null);
        }

        return await base.InvokeAsync(req, ct);
    }
}

public sealed class NodeHandler(INodeDirectory nodes, IClock clock, IConfiguration configuration)
    : IResourceHandler<NodeProjection>
{
    public ValueTask<ResourceEnvelope<NodeProjection>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        var snap = nodes.Get(name);
        return new(snap is null ? null : Envelope(snap, clock));
    }

    public ValueTask<PutOutcome<NodeProjection>> PutAsync(ResourceRequest req, NodeProjection desired, CancellationToken ct) =>
        new(new PutOutcome<NodeProjection>.Rejected(ArmError.MethodNotAllowed("nodes are projected.")));

    public ValueTask<PutOutcome<NodeProjection>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<NodeProjection>.Rejected(ArmError.MethodNotAllowed("nodes are projected.")));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed("nodes are projected.")));

    public ValueTask<PagedResult<ResourceEnvelope<NodeProjection>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var items = nodes.List().Select(n => Envelope(n, clock)).ToList();
        return new(new PagedResult<ResourceEnvelope<NodeProjection>>(items, null));
    }

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (!req.Action.Equals("kill", StringComparison.OrdinalIgnoreCase))
            return new(new ActionOutcome.Rejected(ArmError.ActionNotSupported(req.Action)));
        if (!string.Equals(configuration["Kernel:TestHooks"], "true", StringComparison.OrdinalIgnoreCase)
            && configuration["Kernel:TestHooks"] != "1")
            return new(new ActionOutcome.Rejected(ArmError.Forbidden("kill requires Kernel:TestHooks.")));
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        nodes.Kill(name);
        return new(new ActionOutcome.Ok(new JsonObject { ["killed"] = name }));
    }

    private static ResourceEnvelope<NodeProjection> Envelope(NodeSnapshot snap, IClock clock) =>
        new(
            $"/providers/{AgentsConstants.Namespace}/nodes/{snap.NodeId}",
            snap.NodeId,
            $"{AgentsConstants.Namespace}/nodes",
            "local",
            new Dictionary<string, string>(),
            new NodeProjection
            {
                NodeId = snap.NodeId,
                LastHeartbeat = snap.LastHeartbeat,
                CapacityConfigured = snap.Capacity.Configured,
                CapacityCurrent = snap.Capacity.Current,
                Status = snap.Status,
            },
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
            "W/\"1\"");
}

/// <summary>
/// `ledgers`: the durable budget ledger's own days, projected read-only. The day resource lives
/// outside tenancy (like a node's), so the day name segment is the lookup, never the caller's
/// subscription.
/// </summary>
public sealed class LedgerHandler(IResourceStore store, IClock clock, IConfiguration configuration)
    : IResourceHandler<LedgerDayProjection>
{
    public async ValueTask<ResourceEnvelope<LedgerDayProjection>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        if (!TryParseDay(name, out var day))
            return null;
        var env = await store.GetAsync(DurableBudgetLedger.DayKey(day), ct);
        return env is null ? null : Project(env, day, clock, configuration);
    }

    public ValueTask<PutOutcome<LedgerDayProjection>> PutAsync(ResourceRequest req, LedgerDayProjection desired, CancellationToken ct) =>
        new(new PutOutcome<LedgerDayProjection>.Rejected(ArmError.MethodNotAllowed("ledgers are written by the runtime.")));

    public ValueTask<PutOutcome<LedgerDayProjection>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<LedgerDayProjection>.Rejected(ArmError.MethodNotAllowed("ledgers are written by the runtime.")));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed("ledgers are written by the runtime.")));

    public async ValueTask<PagedResult<ResourceEnvelope<LedgerDayProjection>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var page = await store.ListAsync(new ResourceStoreQuery(
            null,
            null,
            AgentsConstants.Namespace,
            DurableBudgetLedger.DayType,
            req.Top,
            req.ContinuationToken,
            DurableBudgetLedger.DayPrefix), ct);
        var days = new List<ResourceEnvelope<LedgerDayProjection>>();
        foreach (var env in page.Value)
        {
            if (TryParseDay(env.Name, out var day))
                days.Add(Project(env, day, clock, configuration));
        }

        return new PagedResult<ResourceEnvelope<LedgerDayProjection>>(days, page.NextLink);
    }

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
        new(new ActionOutcome.Rejected(ArmError.ActionNotSupported(req.Action)));

    private static bool TryParseDay(string name, out DateOnly day) =>
        DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static ResourceEnvelope<LedgerDayProjection> Project(
        ResourceEnvelope<JsonObject> env,
        DateOnly day,
        IClock clock,
        IConfiguration configuration)
    {
        var bookings = (env.Properties.Deserialize<DurableBudgetLedger.DayEnvelope>(JsonWire.Options) ?? new DurableBudgetLedger.DayEnvelope()).Bookings;
        var spent = bookings.Sum(b => b.Usd);
        // M10 H3: the ceiling travels WITH the spend, read from configuration at this instant. One field on
        // the answer is what lets the console show `spent / stop` without the page holding a second copy of a
        // number only the host can enforce.
        var stop = DailyHardStop.Configured(configuration);
        return new ResourceEnvelope<LedgerDayProjection>(
            env.Id,
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            env.Type,
            env.Location,
            env.Tags,
            new LedgerDayProjection
            {
                Day = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SpentUsd = spent,
                JobCount = bookings.Select(b => b.JobId).Distinct().Count(),
                Bookings = bookings,
                HardStopUsd = stop,
                HardStopReached = stop is { } ceiling && spent >= ceiling,
            },
            ProvisioningState.Succeeded,
            env.SystemData,
            env.ETag);
    }
}
