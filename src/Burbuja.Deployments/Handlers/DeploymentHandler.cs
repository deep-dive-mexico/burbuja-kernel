using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Arm;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Execution;
using Burbuja.Deployments.Targets;

namespace Burbuja.Deployments.Handlers;

public sealed class DeploymentHandler(
    IResourceStore store,
    IClock clock,
    TargetRegistry targets,
    IOperationTracker ops,
    IEventLog events,
    IOutbox outbox,
    CancellationRegistry cancel) : ChildEnvelopeHandler<DeploymentProperties>(store, clock)
{
    protected override string TypeName => $"{DeploymentsConstants.Namespace}/environments/deployments";

    public override async ValueTask<PutOutcome<DeploymentProperties>> PutAsync(ResourceRequest req, DeploymentProperties desired, CancellationToken ct)
    {
        var parent = req.Id.Parent ?? throw new ArmException(ArmError.NotFound("Missing environment.", req.Id.Canonical));
        var envRaw = await Store.GetAsync(parent.Key, ct);
        if (envRaw is null)
            return new PutOutcome<DeploymentProperties>.Rejected(ArmError.NotFound($"Parent '{parent.Canonical}' was not found.", parent.Canonical));
        var environment = envRaw.Properties.Deserialize<EnvironmentProperties>(JsonWire.Options) ?? new EnvironmentProperties();
        if (!targets.TryGet(environment.Target.Kind, out var target))
            return new PutOutcome<DeploymentProperties>.Rejected(ArmError.Validation("UnsupportedTarget", [new ArmError("UnsupportedTarget", $"Unknown target '{environment.Target.Kind}'.", "/properties/target/kind", [], null, 400)]));

        var plan = DeploymentPlanner.Validate(desired.Template, target.SupportedResourceKinds.ToHashSet(), desired.Mode);
        if (plan.Errors.Count > 0)
            return new PutOutcome<DeploymentProperties>.Rejected(ArmError.Validation("The template is invalid.", plan.Errors));

        var hash = TemplateHash.Compute(desired);
        var existingRaw = await Store.GetAsync(req.Id.Key, ct);
        var existing = existingRaw is null ? null : Map(existingRaw);
        if (existing is not null && !DeploymentExecution.IsTerminal(existing.Properties.ExecutionState))
        {
            if (string.Equals(existing.Properties.TemplateHash, hash, StringComparison.OrdinalIgnoreCase))
                return new PutOutcome<DeploymentProperties>.Accepted(existing, existing.Properties.OperationId ?? "", TimeSpan.FromSeconds(2));
            return new PutOutcome<DeploymentProperties>.Rejected(ArmError.OperationInProgress("A deployment is already running."));
        }

        var runId = existing is null ? 1 : existing.Properties.RunId + 1;
        var waves = plan.Waves;
        var planned = await target.PlanAsync(new TargetPlanRequest(parent.Canonical, desired.Mode, desired.Template.Resources), ct);
        var waveOf = waves.SelectMany((w, i) => w.Select(n => (n, i))).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        var steps = planned.Changes.Select(c => new DeploymentStep
        {
            Resource = c.Resource,
            Kind = c.Kind,
            Change = c.Change,
            Wave = c.Change == StepChange.Delete ? waves.Count : waveOf.GetValueOrDefault(c.Resource, 0),
            Status = StepStatus.Pending,
        }).ToList();

        var opId = Guid.NewGuid().ToString("N");
        desired.TemplateHash = hash;
        desired.OperationId = opId;
        desired.RunId = runId;
        desired.ExecutionState = DeploymentExecutionState.Pending;
        desired.Steps = steps;
        desired.PercentComplete = 0;
        desired.Error = null;
        desired.PendingApprovalId = null;
        desired.StartedAt = null;
        desired.EndedAt = null;
        desired.Outputs = null;

        var stored = await UpsertAcceptedAsync(req, desired, existingRaw, ct);
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", stored.Id, null, ct);
        await events.AppendAsync(new EventAppend(DeploymentsConstants.StreamId(stored.Id), stored.Name, DeploymentEvents.Accepted, "accepted", null, RenderLane.Output | RenderLane.Log, stored.Name, null, DeploymentsConstants.StreamType), ct);
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), DeploymentsConstants.TopicExecute, new JsonObject
        {
            ["deploymentKey"] = req.Id.Key,
            ["environmentKey"] = parent.Key,
        }), ct);
        return new PutOutcome<DeploymentProperties>.Accepted(stored, opId, TimeSpan.FromSeconds(2));
    }

    public override ValueTask<PutOutcome<DeploymentProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<DeploymentProperties>.Rejected(ArmError.MethodNotAllowed("Deployments are immutable; re-PUT to change.")));

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(req.Id.Key, ct);
        if (existingRaw is null)
            return new DeleteOutcome.NotFound();
        var existing = Map(existingRaw);
        if (!DeploymentExecution.IsTerminal(existing.Properties.ExecutionState)
            && existing.Properties.ExecutionState != DeploymentExecutionState.Pending)
            return new DeleteOutcome.Rejected(ArmError.OperationInProgress("A deployment is already running."));

        var parent = req.Id.Parent ?? throw new ArmException(ArmError.NotFound("Missing environment.", req.Id.Canonical));
        var envRaw = await Store.GetAsync(parent.Key, ct);
        if (envRaw is null)
            return new DeleteOutcome.Rejected(ArmError.NotFound($"Parent '{parent.Canonical}' was not found.", parent.Canonical));
        var environment = envRaw.Properties.Deserialize<EnvironmentProperties>(JsonWire.Options) ?? new EnvironmentProperties();
        if (!targets.TryGet(environment.Target.Kind, out var target))
            return new DeleteOutcome.Rejected(ArmError.Validation("UnsupportedTarget", []));

        var empty = new DeploymentProperties
        {
            Template = new DeploymentTemplate { Resources = [] },
            Parameters = existing.Properties.Parameters,
            Mode = DeploymentMode.Complete,
            RequiresApproval = existing.Properties.RequiresApproval,
        };
        var planned = await target.PlanAsync(new TargetPlanRequest(parent.Canonical, DeploymentMode.Complete, []), ct);
        var opId = Guid.NewGuid().ToString("N");
        empty.TemplateHash = TemplateHash.Compute(empty);
        empty.OperationId = opId;
        empty.RunId = existing.Properties.RunId + 1;
        empty.ExecutionState = DeploymentExecutionState.Pending;
        empty.Steps = planned.Changes.Select(c => new DeploymentStep
        {
            Resource = c.Resource,
            Kind = c.Kind,
            Change = c.Change,
            Wave = 0,
            Status = StepStatus.Pending,
        }).ToList();
        await UpsertAcceptedAsync(req, empty, existingRaw, ct);
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", existing.Id, null, ct);
        await events.AppendAsync(new EventAppend(DeploymentsConstants.StreamId(existing.Id), existing.Name, DeploymentEvents.Accepted, "accepted", null, RenderLane.Log, existing.Name, null, DeploymentsConstants.StreamType), ct);
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), DeploymentsConstants.TopicExecute, new JsonObject
        {
            ["deploymentKey"] = req.Id.Key,
            ["environmentKey"] = parent.Key,
        }), ct);
        return new DeleteOutcome.Accepted(opId, TimeSpan.FromSeconds(2));
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (req.Action.Equals("validate", StringComparison.OrdinalIgnoreCase))
            return await ValidateAsync(req, ct);
        if (req.Action.Equals("whatIf", StringComparison.OrdinalIgnoreCase))
            return await WhatIfAsync(req, ct);
        if (req.Action.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            return await CancelAsync(req, ct);
        return await base.InvokeAsync(req, ct);
    }

    private async Task<ActionOutcome> ValidateAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var desired = ReadBody(req) ?? new DeploymentProperties();
        var parent = req.Id.Parent;
        var envRaw = parent is null ? null : await Store.GetAsync(parent.Key, ct);
        var environment = envRaw?.Properties.Deserialize<EnvironmentProperties>(JsonWire.Options) ?? new EnvironmentProperties();
        if (!targets.TryGet(environment.Target.Kind, out var target))
            return new ActionOutcome.Ok(new JsonObject { ["valid"] = false, ["errors"] = new JsonArray("UnsupportedTarget") });
        var plan = DeploymentPlanner.Validate(desired.Template, target.SupportedResourceKinds.ToHashSet(), desired.Mode);
        return new ActionOutcome.Ok(new JsonObject
        {
            ["valid"] = plan.Errors.Count == 0,
            ["errors"] = new JsonArray(plan.Errors.Select(e => JsonValue.Create(e.Message)).ToArray()),
        });
    }

    private async Task<ActionOutcome> WhatIfAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var desired = ReadBody(req) ?? new DeploymentProperties();
        var parent = req.Id.Parent ?? req.Id;
        var envRaw = await Store.GetAsync(parent.Key, ct);
        var environment = envRaw?.Properties.Deserialize<EnvironmentProperties>(JsonWire.Options) ?? new EnvironmentProperties();
        if (!targets.TryGet(environment.Target.Kind, out var target))
            return new ActionOutcome.Rejected(ArmError.Validation("UnsupportedTarget", []));
        var plan = DeploymentPlanner.Validate(desired.Template, target.SupportedResourceKinds.ToHashSet(), desired.Mode);
        if (plan.Errors.Count > 0)
            return new ActionOutcome.Rejected(ArmError.Validation("The template is invalid.", plan.Errors));
        var waves = DeploymentPlanner.Waves(desired.Template.Resources);
        var planned = await target.PlanAsync(new TargetPlanRequest(parent.Canonical, desired.Mode, desired.Template.Resources), ct);
        return new ActionOutcome.Ok(new JsonObject
        {
            ["changes"] = new JsonArray(planned.Changes.Select(c => new JsonObject { ["resource"] = c.Resource, ["change"] = c.Change.ToString() }).ToArray()),
            ["waves"] = new JsonArray(waves.Select(w => new JsonArray(w.Select(n => JsonValue.Create(n)).ToArray())).ToArray()),
        });
    }

    private async Task<ActionOutcome> CancelAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        if (DeploymentExecution.IsTerminal(existing.Properties.ExecutionState))
            return new ActionOutcome.Rejected(ArmError.Conflict("Canceled", "The deployment is already terminal."));
        existing.Properties.ExecutionState = DeploymentExecutionState.Canceling;
        await Store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                existing.Id, existing.Name, existing.Type, existing.Location, existing.Tags,
                JsonSerializer.SerializeToNode(existing.Properties, JsonWire.Options)!.AsObject(),
                ProvisioningState.Deleting, existing.SystemData, existing.ETag),
            existing.ETag, ct);
        cancel.Cancel(StoreKey(req.Id));
        await events.AppendAsync(new EventAppend(DeploymentsConstants.StreamId(existing.Id), existing.Name, DeploymentEvents.Canceling, "canceling", null, RenderLane.Log, existing.Name, null, DeploymentsConstants.StreamType), ct);
        return new ActionOutcome.Accepted(existing.Properties.OperationId ?? "", TimeSpan.FromSeconds(1), null);
    }

    private async Task<ResourceEnvelope<DeploymentProperties>> UpsertAcceptedAsync(
        ResourceRequest req,
        DeploymentProperties desired,
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

    private static DeploymentProperties? ReadBody(ResourceActionRequest req)
    {
        if (req.Body is null)
            return null;
        var props = req.Body["properties"] as JsonObject ?? req.Body;
        return props.Deserialize<DeploymentProperties>(JsonWire.Options);
    }
}
