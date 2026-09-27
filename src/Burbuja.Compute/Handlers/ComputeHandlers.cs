using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Arm;
using Burbuja.Core.Process;
using Burbuja.Core.Sdk.Policy;
using Microsoft.Extensions.Options;

namespace Burbuja.Compute.Handlers;

public sealed class ComputeWorkspaceHandler(
    IResourceStore store,
    IClock clock,
    BackendRegistry backends,
    IOperationTracker ops,
    IEventLog events,
    IOutbox outbox,
    IPolicyPipeline<ActionPolicySubject> policy,
    IExecutionGrantAuthority grants,
    IApprovalService approvals,
    IOptions<ComputeOptions> options) : EnvelopeHandler<ComputeWorkspaceProperties>(store, clock)
{
    protected override string TypeName => $"{ComputeConstants.Namespace}/workspaces";

    public override async ValueTask<PutOutcome<ComputeWorkspaceProperties>> PutAsync(ResourceRequest req, ComputeWorkspaceProperties desired, CancellationToken ct)
    {
        if (!backends.TryGet(desired.Backend, out var backend))
            return Reject("UnsupportedBackend", $"Unknown backend '{desired.Backend}'.");
        if (!backend.SupportedImages.Contains(desired.Image, StringComparer.OrdinalIgnoreCase))
            return Reject("UnsupportedImage", $"Backend '{desired.Backend}' does not offer image '{desired.Image}'.");
        if (!backend.SupportedSizes.Contains(desired.Size, StringComparer.OrdinalIgnoreCase))
            return Reject("UnsupportedSize", $"Backend '{desired.Backend}' does not offer size '{desired.Size}'.");

        var hasAgentsWorkspace = !string.IsNullOrWhiteSpace(desired.Repo.AgentsWorkspaceId);
        var hasRemote = !string.IsNullOrWhiteSpace(desired.Repo.Remote);
        if (hasAgentsWorkspace && hasRemote)
            return Reject("InvalidRepoBinding", "Bind either an Agents workspace id or a remote, not both.");
        if (hasAgentsWorkspace && !desired.Repo.AgentsWorkspaceId!.StartsWith('/'))
            return Reject("InvalidRepoBinding", "agentsWorkspaceId must be an ARM resource id.");
        if (hasRemote && !desired.Repo.Remote!.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !desired.Repo.Remote.Contains('@'))
            return Reject("InvalidRepoBinding", "remote must be an http(s) or ssh git URL.");

        foreach (var key in desired.EnvAllowlist)
        {
            if (ProcessEnvironmentPolicy.IsDenied(key))
                return Reject("DeniedEnvKey", $"Environment key '{key}' is never allowed.");
            if (!ProcessEnvironmentPolicy.Allowlist.Contains(key))
                return Reject("UnknownEnvKey", $"Environment key '{key}' is not in the process allowlist.");
        }

        if (desired.Ttl is { } ttl && ttl > options.Value.MaxTtl)
            return Reject("TtlTooLong", $"ttl must not exceed {options.Value.MaxTtl}.");

        var hash = ComputeSpecHash.Compute(desired);
        var existingRaw = await Store.GetAsync(req.Id.Key, ct);
        var existing = existingRaw is null ? null : Map(existingRaw);
        if (existing is not null && ComputeLifecycle.IsActive(existing.Properties.State))
        {
            if (string.Equals(existing.Properties.SpecHash, hash, StringComparison.OrdinalIgnoreCase))
                return new PutOutcome<ComputeWorkspaceProperties>.Accepted(existing, existing.Properties.OperationId ?? "", TimeSpan.FromSeconds(2));
            return new PutOutcome<ComputeWorkspaceProperties>.Rejected(ArmError.OperationInProgress("A workspace operation is already running."));
        }

        var opId = Guid.NewGuid().ToString("N");
        desired.State = ComputeWorkspaceState.Creating;
        desired.OperationId = opId;
        desired.SpecHash = hash;
        desired.Generation = (existing?.Properties.Generation ?? 0) + 1;
        desired.Error = null;
        desired.ProvisionedAt = null;
        desired.BackendHandle = null;
        desired.RootPath = null;
        desired.Branch = null;
        desired.BaseSha = null;

        var stored = await UpsertAsync(req, desired, existingRaw, ProvisioningState.Accepted, ct);
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", stored.Id, null, ct);
        await AppendAsync(stored, ComputeEvents.WorkspaceAccepted, "accepted", ct);
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), ComputeConstants.TopicLifecycle, new JsonObject
        {
            ["workspaceKey"] = req.Id.Key,
            ["op"] = "provision",
        }), ct);
        return new PutOutcome<ComputeWorkspaceProperties>.Accepted(stored, opId, TimeSpan.FromSeconds(2));
    }

    public override ValueTask<PutOutcome<ComputeWorkspaceProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<ComputeWorkspaceProperties>.Rejected(ArmError.MethodNotAllowed("Compute workspace specs are immutable; re-PUT to change.")));

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(req.Id.Key, ct);
        if (existingRaw is null)
            return new DeleteOutcome.NotFound();
        var existing = Map(existingRaw);
        if (ComputeLifecycle.IsActive(existing.Properties.State))
            return new DeleteOutcome.Rejected(ArmError.Conflict("OperationInProgress", "Stop or deallocate the workspace before deleting it."));

        var opId = Guid.NewGuid().ToString("N");
        existing.Properties.State = ComputeWorkspaceState.Deleting;
        existing.Properties.OperationId = opId;
        existing.Properties.Generation += 1;
        await PersistAsync(existing, ProvisioningState.Deleting, ct);
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", existing.Id, null, ct);
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), ComputeConstants.TopicLifecycle, new JsonObject
        {
            ["workspaceKey"] = req.Id.Key,
            ["op"] = "destroy",
        }), ct);
        return new DeleteOutcome.Accepted(opId, TimeSpan.FromSeconds(2));
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var action = req.Action.ToLowerInvariant();
        if (action == "exec")
            return await ExecAsync(req, ct);
        if (action == "probe")
            return await ProbeAsync(req, ct);
        if (action is not ("start" or "stop" or "deallocate" or "snapshot"))
            return await base.InvokeAsync(req, ct);
        return await TransitionAsync(req, action, ct);
    }

    private async ValueTask<ActionOutcome> TransitionAsync(ResourceActionRequest req, string action, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        var state = existing.Properties.State;

        var allowed = action switch
        {
            "start" => state is ComputeWorkspaceState.Stopped or ComputeWorkspaceState.Deallocated or ComputeWorkspaceState.Failed,
            "stop" => state is ComputeWorkspaceState.Running,
            "deallocate" => state is ComputeWorkspaceState.Running or ComputeWorkspaceState.Stopped,
            "snapshot" => state is ComputeWorkspaceState.Running or ComputeWorkspaceState.Stopped,
            _ => false,
        };
        if (!allowed)
            return new ActionOutcome.Rejected(ArmError.Conflict("InvalidState", $"Cannot {action} a workspace in state {state}."));

        var opId = Guid.NewGuid().ToString("N");
        existing.Properties.OperationId = opId;
        await PersistAsync(existing, ProvisioningState.Accepted, ct);
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", existing.Id, null, ct);
        var payload = new JsonObject
        {
            ["workspaceKey"] = StoreKey(req.Id),
            ["op"] = action,
        };
        if (action == "snapshot")
        {
            var name = req.Body?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                return new ActionOutcome.Rejected(ArmError.Validation("snapshot requires a name.", []));
            payload["name"] = name;
        }

        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), ComputeConstants.TopicLifecycle, payload), ct);
        return new ActionOutcome.Accepted(opId, TimeSpan.FromSeconds(2), null);
    }

    private async ValueTask<ActionOutcome> ProbeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        if (!backends.TryGet(existing.Properties.Backend, out var backend))
            return new ActionOutcome.Rejected(ArmError.Validation("UnsupportedBackend", []));
        var probe = await backend.ProbeAsync(ct);
        return new ActionOutcome.Ok(new JsonObject { ["ready"] = probe.Ready, ["detail"] = probe.Detail, ["state"] = existing.Properties.State.ToString() });
    }

    private async ValueTask<ActionOutcome> ExecAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var existingRaw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (existingRaw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var existing = Map(existingRaw);
        if (existing.Properties.State != ComputeWorkspaceState.Running)
            return new ActionOutcome.Rejected(ArmError.Conflict("InvalidState", $"exec requires a running workspace (state is {existing.Properties.State})."));
        if (!backends.TryGet(existing.Properties.Backend, out var backend))
            return new ActionOutcome.Rejected(ArmError.Validation("UnsupportedBackend", []));
        // RootPath is never persisted (it must not reach the wire), so re-derive it from the key.
        var root = existing.Properties.RootPath is { Length: > 0 } storedRoot
            ? storedRoot
            : backend.ResolveRootPath(StoreKey(req.Id));

        var body = req.Body ?? new JsonObject();
        var command = body["command"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(command))
            return new ActionOutcome.Rejected(ArmError.Validation("exec requires a command.", []));
        var cwd = body["cwd"]?.GetValue<string>() ?? ".";
        var timeoutSeconds = body["timeoutSeconds"]?.GetValue<int>() ?? 60;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body["env"] is JsonObject envObj)
        {
            foreach (var (key, value) in envObj)
                environment[key] = value?.GetValue<string>() ?? "";
        }

        var streamId = ComputeConstants.StreamId(existing.Id);
        var input = new JsonObject { ["command"] = command, ["cwd"] = cwd };
        // Audit before decision: the intent event's seq is strictly lower than the decision's.
        await events.AppendAsync(new EventAppend(streamId, existing.Name, ComputeEvents.PolicyIntent, command, input.DeepClone(), RenderLane.Log, existing.Name, null, ComputeConstants.StreamType), ct);
        var context = new PolicyContext(existing.Id, root, root, PolicyProfileParser.Parse(options.Value.Profile), "local", root, [], [], []);
        var verdict = policy.Classify(new ActionPolicySubject("shell", input, context));
        await events.AppendAsync(new EventAppend(streamId, existing.Name, ComputeEvents.PolicyDecision, verdict.Tier.ToString(), new JsonObject
        {
            ["tier"] = verdict.Tier.ToString(),
            ["class"] = verdict.Finding.Class,
            ["reason"] = verdict.Finding.Reason,
        }, RenderLane.Log, existing.Name, null, ComputeConstants.StreamType), ct);

        if (verdict.Tier == RiskTier.HardDeny)
            return new ActionOutcome.Rejected(new ArmError("PolicyDenied", verdict.Finding.Reason, null, [], null, 403));

        if (verdict.Tier is RiskTier.Gray or RiskTier.Escalate)
        {
            var approvalId = await approvals.OpenAsync(new ApprovalRequest(
                streamId,
                existing.Name,
                ApprovalKind.Permission,
                "compute-workspace",
                // M11 S1 — `existing.Id` IS the canonical path (the envelope's first field), so "who may run
                // a shell here" is answered by the subject's scope rather than by an unscoped verb.
                existing.Id,
                "compute:exec:" + ComputeSpecHash.Compute(existing.Properties),
                null,
                verdict.Finding.Class,
                "exec",
                command,
                verdict.Finding.Reason,
                [],
                true,
                TimeSpan.FromMinutes(15)), ct);
            return new ActionOutcome.Accepted(approvalId, TimeSpan.FromSeconds(5), null);
        }

        var grant = grants.Mint(existing.Id, "compute:exec", TimeSpan.FromMinutes(10));
        var result = await backend.ExecAsync(new ComputeExecRequest(
            root,
            command,
            cwd,
            environment,
            existing.Properties.EnvAllowlist,
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)),
            options.Value.ExecMaxOutputBytes), grant, ct);
        await events.AppendAsync(new EventAppend(streamId, existing.Name, ComputeEvents.ExecFinished, $"exit {result.ExitCode}", new JsonObject
        {
            ["exitCode"] = result.ExitCode,
            ["timedOut"] = result.TimedOut,
            ["durationMs"] = result.DurationMs,
        }, RenderLane.Output | RenderLane.Log, existing.Name, null, ComputeConstants.StreamType), ct);
        return new ActionOutcome.Ok(new JsonObject
        {
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["timedOut"] = result.TimedOut,
            ["durationMs"] = result.DurationMs,
        });
    }

    private static PutOutcome<ComputeWorkspaceProperties> Reject(string code, string message) =>
        new PutOutcome<ComputeWorkspaceProperties>.Rejected(ArmError.Validation(code, [new ArmError(code, message, null, [], null, 400)]));

    private async Task<ResourceEnvelope<ComputeWorkspaceProperties>> UpsertAsync(
        ResourceRequest req,
        ComputeWorkspaceProperties desired,
        ResourceEnvelope<JsonObject>? existing,
        ProvisioningState provisioningState,
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
            provisioningState,
            existing is null
                ? new SystemData(now, req.Principal.PrincipalId, req.Principal.PrincipalType, now, req.Principal.PrincipalId, req.Principal.PrincipalType)
                : existing.SystemData with { LastModifiedAt = now, LastModifiedBy = req.Principal.PrincipalId, LastModifiedByType = req.Principal.PrincipalType },
            existing?.ETag ?? "");
        return Map(await Store.UpsertAsync(envelope, req.IfMatch ?? existing?.ETag, ct));
    }

    private async Task PersistAsync(ResourceEnvelope<ComputeWorkspaceProperties> envelope, ProvisioningState provisioningState, CancellationToken ct)
    {
        var json = ComputeStateStore.ToJson(envelope with { ProvisioningState = provisioningState });
        await Store.UpsertAsync(json, envelope.ETag, ct);
    }

    private async Task AppendAsync(ResourceEnvelope<ComputeWorkspaceProperties> env, string type, string summary, CancellationToken ct) =>
        await events.AppendAsync(new EventAppend(
            ComputeConstants.StreamId(env.Id),
            env.Name,
            type,
            summary,
            null,
            RenderLane.Output | RenderLane.Log,
            env.Name,
            null,
            ComputeConstants.StreamType), ct);
}

public sealed class ComputeSnapshotHandler(IResourceStore store, IClock clock) : ChildEnvelopeHandler<ComputeSnapshotProperties>(store, clock)
{
    protected override string TypeName => $"{ComputeConstants.Namespace}/workspaces/snapshots";

    public override ValueTask<PutOutcome<ComputeSnapshotProperties>> PutAsync(ResourceRequest req, ComputeSnapshotProperties desired, CancellationToken ct) =>
        new(new PutOutcome<ComputeSnapshotProperties>.Rejected(ArmError.MethodNotAllowed("Snapshots are created by the workspace snapshot action.")));

    public override ValueTask<PutOutcome<ComputeSnapshotProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<ComputeSnapshotProperties>.Rejected(ArmError.MethodNotAllowed("Snapshots are immutable.")));

    // DELETE removes the row. The artifact itself is left in place: IArtifactStore is append-only.
}

/// <summary>Projects the provider's subscription-less catalogue: backends, images, sizes.</summary>
public sealed class ComputeCatalogHandler(BackendRegistry backends, IClock clock) : IResourceHandler<ComputeBackendProjection>
{
    public ValueTask<ResourceEnvelope<ComputeBackendProjection>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        var projection = Project(req.Id.FullyQualifiedType, name);
        return new(projection is null ? null : Envelope(req.Id.FullyQualifiedType, projection, clock));
    }

    public ValueTask<PutOutcome<ComputeBackendProjection>> PutAsync(ResourceRequest req, ComputeBackendProjection desired, CancellationToken ct) =>
        new(new PutOutcome<ComputeBackendProjection>.Rejected(ArmError.MethodNotAllowed("the catalogue is projected.")));

    public ValueTask<PutOutcome<ComputeBackendProjection>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<ComputeBackendProjection>.Rejected(ArmError.MethodNotAllowed("the catalogue is projected.")));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed("the catalogue is projected.")));

    public ValueTask<PagedResult<ResourceEnvelope<ComputeBackendProjection>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var items = ProjectAll(req.Id.FullyQualifiedType).Select(p => Envelope(req.Id.FullyQualifiedType, p, clock)).ToList();
        return new(new PagedResult<ResourceEnvelope<ComputeBackendProjection>>(items, null));
    }

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (!req.Action.Equals("probe", StringComparison.OrdinalIgnoreCase))
            return new(new ActionOutcome.Rejected(ArmError.ActionNotSupported(req.Action)));
        _ = backends;
        return new(new ActionOutcome.Ok(new JsonObject { ["ready"] = true, ["detail"] = "catalogue" }));
    }

    private ComputeBackendProjection? Project(string type, string name) =>
        ProjectAll(type).FirstOrDefault(p => p.Kind.Equals(name, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<ComputeBackendProjection> ProjectAll(string type) => type switch
    {
        "images" => backends.All
            .SelectMany(b => b.SupportedImages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i, StringComparer.Ordinal)
            .Select(i => new ComputeBackendProjection { Kind = i }),
        "sizes" => backends.All
            .SelectMany(b => b.SupportedSizes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => new ComputeBackendProjection { Kind = s }),
        _ => backends.All
            .OrderBy(b => b.Kind, StringComparer.Ordinal)
            .Select(b => new ComputeBackendProjection
            {
                Kind = b.Kind,
                SupportedImages = b.SupportedImages.ToList(),
                SupportedSizes = b.SupportedSizes.ToList(),
            }),
    };

    private static ResourceEnvelope<ComputeBackendProjection> Envelope(string type, ComputeBackendProjection projection, IClock clock) =>
        new(
            $"/providers/{ComputeConstants.Namespace}/{type}/{projection.Kind}",
            projection.Kind,
            $"{ComputeConstants.Namespace}/{type}",
            "local",
            new Dictionary<string, string>(),
            projection,
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
            "W/\"1\"");
}
