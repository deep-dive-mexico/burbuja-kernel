using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Arm;
using Microsoft.Extensions.Configuration;

namespace Burbuja.Agents.Handlers;

public sealed class JobHandler(
    IResourceStore store,
    IClock clock,
    IOperationTracker ops,
    IEventLog events,
    IOutbox outbox,
    CancellationRegistry cancel,
    SteeringInbox steering,
    StrategyRegistry strategies,
    ICostCalculator costs,
    IConfiguration configuration) : ChildEnvelopeHandler<JobProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/workspaces/jobs";

    public override async ValueTask<PutOutcome<JobProperties>> PutAsync(ResourceRequest req, JobProperties desired, CancellationToken ct)
    {
        var errors = Validate(desired.Spec);
        if (errors.Count > 0)
            return new PutOutcome<JobProperties>.Rejected(ArmError.Validation("The job spec is invalid.", errors));

        // A job with no priced model is rejected here, at PUT — its turns could never be quoted.
        // Agents are deliberately not checked: their default is deepseek-flash, which a host that
        // never staged the DeepSeek price book cannot price, and that is unknown cost, not a bad
        // request.
        var modelId = ResolveModelId(desired.ModelId);
        if (costs.TryGet(modelId) is null)
            return Reject("UnpricedModel", $"The model '{modelId}' has no price book on this host.");

        var scenarioTag = req.Tags.TryGetValue(AgentsConstants.ScenarioTag, out var scenario) ? scenario : null;
        var strategyId = StrategyIds.Normalize(desired.Spec.Strategy, scenarioTag);
        if (!strategies.TryGet(strategyId, out _))
            return new PutOutcome<JobProperties>.Rejected(new ArmError(
                "UnknownStrategy",
                $"Unknown orchestration strategy '{strategyId}'.",
                "/properties/spec/strategy",
                [],
                null,
                400));

        var hash = SpecHash.Compute(desired.Spec, req.Tags);
        var existingRaw = await Store.GetAsync(req.Id.Key, ct);
        var existing = existingRaw is null ? null : Map(existingRaw);
        if (existing is not null && !JobExecution.IsTerminal(existing.Properties.Status))
        {
            if (string.Equals(existing.Properties.SpecHash, hash, StringComparison.OrdinalIgnoreCase))
                return new PutOutcome<JobProperties>.Accepted(existing, existing.Properties.OperationId ?? "", TimeSpan.FromSeconds(2));
            return new PutOutcome<JobProperties>.Rejected(ArmError.OperationInProgress("A job is already running."));
        }

        var opId = Guid.NewGuid().ToString("N");
        desired.SpecHash = hash;
        desired.OperationId = opId;
        desired.RunId = existing is null ? 1 : existing.Properties.RunId + 1;
        desired.Status = desired.Spec.AutoAccept ? JobExecutionStatus.Queued : JobExecutionStatus.Paused;
        desired.Cost = new JobCost(0, 0, 0, 0, 0, 0);
        desired.Error = null;
        desired.PendingApprovalId = null;
        desired.StartedAt = null;
        desired.EndedAt = null;
        desired.RunAttempts = 0;
        desired.Recovery = null;
        desired.State = null;
        desired.ActiveRunId = null;
        desired.GroupId = null;
        desired.CostIsPartial = false;
        desired.Budgets ??= new JobBudgets(
            desired.Spec.MaxBudgetUsd ?? ReadDecimal(configuration, "Budget:DefaultJobUsd", 5m),
            desired.Spec.MaxTurns ?? 80,
            ReadDecimal(configuration, "Budget:DailySoftCapUsd", 0m),
            ReadDecimal(configuration, "Budget:DailyHardStopUsd", 0m));
        desired.WorkspaceRevision = "1";
        cancel.Reset(req.Id.Key);

        var stored = await UpsertAcceptedAsync(req, desired, existingRaw, ct);
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", stored.Id, null, ct);
        await events.AppendAsync(new EventAppend(AgentsConstants.StreamId(stored.Id), stored.Name, AgentEventTypes.Init, "accepted", null, RenderLane.Output | RenderLane.Log, stored.Name, null, AgentsConstants.StreamType), ct);
        if (desired.Spec.AutoAccept)
        {
            await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicRun, new JsonObject
            {
                ["jobKey"] = req.Id.Key,
            }), ct);
        }

        return new PutOutcome<JobProperties>.Accepted(stored, opId, TimeSpan.FromSeconds(2));
    }

    public override ValueTask<PutOutcome<JobProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<JobProperties>.Rejected(ArmError.MethodNotAllowed("Jobs are immutable; re-PUT to change.")));

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (req.Action.Equals("accept", StringComparison.OrdinalIgnoreCase))
            return await AcceptAsync(req, ct);
        if (req.Action.Equals("steer", StringComparison.OrdinalIgnoreCase))
            return await SteerAsync(req, ct);
        if (req.Action.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            return await CancelAsync(req, ct);
        if (req.Action.Equals("extendBudget", StringComparison.OrdinalIgnoreCase))
            return await ExtendAsync(req, ct);
        return await base.InvokeAsync(req, ct);
    }

    private async Task<ActionOutcome> AcceptAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        if (JobExecution.IsTerminal(existing.Properties.Status))
            return new ActionOutcome.Rejected(ArmError.Conflict("Canceled", "The job is already terminal."));
        existing.Properties.Status = JobExecutionStatus.Queued;
        await Store.UpsertAsync(ToJson(existing), existing.ETag, ct);
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), AgentsConstants.TopicRun, new JsonObject
        {
            ["jobKey"] = StoreKey(req.Id),
        }), ct);
        return new ActionOutcome.Accepted(existing.Properties.OperationId ?? "", TimeSpan.FromSeconds(1), null);
    }

    private async Task<ActionOutcome> SteerAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var body = req.Body?["properties"] as JsonObject ?? req.Body ?? new JsonObject();
        var text = body["text"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return new ActionOutcome.Rejected(ArmError.Validation("text is required.", []));
        steering.Push(StoreKey(req.Id), text);
        return new ActionOutcome.Ok(new JsonObject { ["queued"] = true });
    }

    private async Task<ActionOutcome> CancelAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        if (JobExecution.IsTerminal(existing.Properties.Status))
            return new ActionOutcome.Rejected(ArmError.Conflict("Canceled", "The job is already terminal."));
        cancel.Cancel(StoreKey(req.Id));
        return new ActionOutcome.Accepted(existing.Properties.OperationId ?? "", TimeSpan.FromSeconds(1), null);
    }

    private async Task<ActionOutcome> ExtendAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        var body = req.Body?["properties"] as JsonObject ?? req.Body ?? new JsonObject();
        var add = body["usd"]?.GetValue<decimal>() ?? 20m;
        var budgets = existing.Properties.Budgets ?? new JobBudgets(5, 80, 0, 0);
        existing.Properties.Budgets = budgets with { MaxUsd = budgets.MaxUsd + add };
        await Store.UpsertAsync(ToJson(existing), existing.ETag, ct);
        return new ActionOutcome.Ok(new JsonObject { ["maxUsd"] = existing.Properties.Budgets.MaxUsd });
    }

    private async Task<ResourceEnvelope<JobProperties>> UpsertAcceptedAsync(
        ResourceRequest req,
        JobProperties desired,
        ResourceEnvelope<JsonObject>? existing,
        CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var envelope = new ResourceEnvelope<JsonObject>(
            req.Id.Canonical,
            req.Id.NameSegments.Length > 0 ? req.Id.NameSegments[^1] : req.Id.ResourceGroup ?? req.Id.SubscriptionId,
            TypeName,
            string.IsNullOrWhiteSpace(req.Location) ? existing?.Location ?? "local" : req.Location,
            new Dictionary<string, string>(req.Tags, StringComparer.OrdinalIgnoreCase),
            JsonSerializer.SerializeToNode(desired, JsonWire.Options)!.AsObject(),
            ProvisioningState.Accepted,
            existing is null
                ? new SystemData(now, req.Principal.PrincipalId, req.Principal.PrincipalType, now, req.Principal.PrincipalId, req.Principal.PrincipalType)
                : existing.SystemData with { LastModifiedAt = now, LastModifiedBy = req.Principal.PrincipalId, LastModifiedByType = req.Principal.PrincipalType },
            existing?.ETag ?? "");
        var stored = await Store.UpsertAsync(envelope, req.IfMatch ?? existing?.ETag, ct);
        return Map(stored);
    }

    private static ResourceEnvelope<JsonObject> ToJson(ResourceEnvelope<JobProperties> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            JsonSerializer.SerializeToNode(env.Properties, JsonWire.Options)!.AsObject(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);

    private static decimal ReadDecimal(IConfiguration configuration, string key, decimal fallback) =>
        decimal.TryParse(configuration[key], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : fallback;

    /// <summary>Same resolution the runner uses: an unset job model is the host's default model.</summary>
    private static string ResolveModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? AgentsConstants.DefaultModelId : modelId;

    private static PutOutcome<JobProperties> Reject(string code, string message) =>
        new PutOutcome<JobProperties>.Rejected(ArmError.Validation(code, [new ArmError(code, message, null, [], null, 400)]));

    private static List<ArmError> Validate(JobSpec spec)
    {
        var errors = new List<ArmError>();
        if (string.IsNullOrWhiteSpace(spec.Title))
            errors.Add(new ArmError("InvalidSpec", "title is required.", "/properties/spec/title", [], null, 400));
        if (string.IsNullOrWhiteSpace(spec.Slug) || !System.Text.RegularExpressions.Regex.IsMatch(spec.Slug, "^[a-z][a-z0-9-]{0,38}$"))
            errors.Add(new ArmError("InvalidSpec", "slug is invalid.", "/properties/spec/slug", [], null, 400));
        if (string.IsNullOrWhiteSpace(spec.Brief))
            errors.Add(new ArmError("InvalidSpec", "brief is required.", "/properties/spec/brief", [], null, 400));
        return errors;
    }
}
