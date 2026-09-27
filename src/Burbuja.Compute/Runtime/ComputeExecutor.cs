using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Burbuja.Compute.Runtime;

/// <summary>Runs the workspace lifecycle operations the handler enqueued on `compute.lifecycle`.</summary>
public sealed class ComputeExecutor(
    IMessageBus bus,
    IResourceStore store,
    ComputeStateStore states,
    BackendRegistry backends,
    IOperationTracker ops,
    IEventLog events,
    IClock clock,
    IOptions<ComputeOptions> options,
    IConfiguration configuration) : IHostedService
{
    private IAsyncDisposable? _sub;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var role = configuration["Kernel:Role"] ?? "all";
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;
        _sub = await bus.SubscribeAsync(ComputeConstants.TopicLifecycle, OnMessage, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sub is not null)
            await _sub.DisposeAsync();
    }

    internal async ValueTask OnMessage(BusMessage message, CancellationToken ct)
    {
        var payload = message.Payload as JsonObject ?? message.Payload.Deserialize<JsonObject>();
        var key = payload?["workspaceKey"]?.GetValue<string>();
        var op = payload?["op"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(op))
            return;
        await RunAsync(key, op, payload ?? new JsonObject(), ct);
    }

    /// <summary>Runs one lifecycle operation; the outbox relay calls it via <see cref="OnMessage"/>.</summary>
    public async ValueTask RunAsync(string key, string op, JsonObject payload, CancellationToken ct)
    {
        var env = await states.GetAsync(key, ct);
        if (env is null)
            return;

        try
        {
            switch (op)
            {
                case "provision":
                case "start":
                    await ProvisionAsync(env, key, ct);
                    break;
                case "stop":
                    await StoppedAsync(env, key, ComputeWorkspaceState.Stopping, ComputeWorkspaceState.Stopped, ComputeEvents.WorkspaceStopped, ct);
                    break;
                case "deallocate":
                    await StoppedAsync(env, key, ComputeWorkspaceState.Deallocating, ComputeWorkspaceState.Deallocated, ComputeEvents.WorkspaceDeallocated, ct);
                    break;
                case "destroy":
                    await DestroyAsync(env, key, ct);
                    break;
                case "snapshot":
                    await SnapshotAsync(env, key, payload["name"]?.GetValue<string>() ?? "", ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            await FailAsync(key, env.Properties.OperationId, ex.Message, ct);
        }
    }

    private async ValueTask ProvisionAsync(ResourceEnvelope<ComputeWorkspaceProperties> env, string key, CancellationToken ct)
    {
        if (!backends.TryGet(env.Properties.Backend, out var backend))
        {
            await FailAsync(key, env.Properties.OperationId, $"Unknown backend '{env.Properties.Backend}'.", ct);
            return;
        }

        await states.MutateAsync(key, p => p.State = ComputeWorkspaceState.Creating, ct, ProvisioningState.Creating);
        var result = await backend.ProvisionAsync(new ComputeProvisionRequest(key, "", env.Properties), ct);
        var now = clock.UtcNow;
        var ttl = env.Properties.Ttl ?? options.Value.MaxTtl;
        await states.MutateAsync(key, p =>
        {
            p.State = ComputeWorkspaceState.Running;
            p.RootPath = result.RootPath;
            p.BackendHandle = result.BackendHandle;
            p.Branch = result.Branch;
            p.BaseSha = result.BaseSha;
            p.ProvisionedAt = now;
            p.LastActivityAt = now;
            p.ExpiresAt = now + ttl;
            p.Error = null;
        }, ct, ProvisioningState.Succeeded);
        await AppendAsync(env.Id, env.Name, ComputeEvents.WorkspaceRunning, "running", ct);
        await CompleteAsync(env.Properties.OperationId, ct);
    }

    private async ValueTask StoppedAsync(
        ResourceEnvelope<ComputeWorkspaceProperties> env,
        string key,
        ComputeWorkspaceState intermediate,
        ComputeWorkspaceState target,
        string eventType,
        CancellationToken ct)
    {
        await states.MutateAsync(key, p => p.State = intermediate, ct, ProvisioningState.Accepted);
        await Task.Delay(1, ct);
        await states.MutateAsync(key, p => p.State = target, ct, ProvisioningState.Succeeded);
        await AppendAsync(env.Id, env.Name, eventType, target.ToString(), ct);
        await CompleteAsync(env.Properties.OperationId, ct);
    }

    private async ValueTask DestroyAsync(ResourceEnvelope<ComputeWorkspaceProperties> env, string key, CancellationToken ct)
    {
        if (backends.TryGet(env.Properties.Backend, out var backend))
        {
            var root = env.Properties.RootPath is { Length: > 0 } stored ? stored : backend.ResolveRootPath(key);
            await backend.DestroyAsync(root, ct);
        }
        await states.MutateAsync(key, p => p.State = ComputeWorkspaceState.Deleted, ct, ProvisioningState.Deleting);
        await AppendAsync(env.Id, env.Name, ComputeEvents.WorkspaceDeleted, "deleted", ct);
        await CompleteAsync(env.Properties.OperationId, ct);
        await store.DeleteAsync(key, null, ct);
    }

    private async ValueTask SnapshotAsync(ResourceEnvelope<ComputeWorkspaceProperties> env, string key, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            await FailAsync(key, env.Properties.OperationId, "snapshot requires a name.", ct);
            return;
        }

        if (!backends.TryGet(env.Properties.Backend, out var backend))
        {
            await FailAsync(key, env.Properties.OperationId, $"Unknown backend '{env.Properties.Backend}'.", ct);
            return;
        }

        var root = env.Properties.RootPath is { Length: > 0 } storedRoot ? storedRoot : backend.ResolveRootPath(key);

        var snapshot = await backend.SnapshotAsync(root, name, ct);
        var parent = env.Id.TrimEnd('/');
        var json = new JsonObject
        {
            ["workspaceName"] = env.Name,
            ["name"] = name,
            ["createdAt"] = clock.UtcNow,
            ["sizeBytes"] = snapshot.SizeBytes,
            ["sha256"] = snapshot.Sha256,
        };
        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                $"{parent}/snapshots/{name}",
                name,
                $"{ComputeConstants.Namespace}/workspaces/snapshots",
                env.Location,
                new Dictionary<string, string>(),
                json,
                ProvisioningState.Succeeded,
                new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
                ""),
            null,
            ct);
        await states.MutateAsync(key, p =>
        {
            p.LastSnapshotName = name;
            p.State = ComputeWorkspaceState.Running;
        }, ct, ProvisioningState.Succeeded);
        await AppendAsync(env.Id, env.Name, ComputeEvents.SnapshotCaptured, name, ct);
        await CompleteAsync(env.Properties.OperationId, ct);
    }

    private async ValueTask FailAsync(string key, string? operationId, string message, CancellationToken ct)
    {
        await states.MutateAsync(key, p =>
        {
            p.State = ComputeWorkspaceState.Failed;
            p.Error = message;
        }, ct, ProvisioningState.Failed);
        if (!string.IsNullOrEmpty(operationId))
            await ops.FailAsync(operationId, ArmError.Conflict("ComputeOperationFailed", message), ct);
    }

    private async ValueTask CompleteAsync(string? operationId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(operationId))
            await ops.SucceedAsync(operationId, null, ct);
    }

    private async ValueTask AppendAsync(string canonicalId, string subject, string type, string summary, CancellationToken ct) =>
        await events.AppendAsync(new EventAppend(
            ComputeConstants.StreamId(canonicalId),
            subject,
            type,
            summary,
            null,
            RenderLane.Output | RenderLane.Log,
            subject,
            null,
            ComputeConstants.StreamType), ct);
}

/// <summary>SSE stream-id shape for the compute namespace (C1).</summary>
public sealed class ComputeEventStreams : IEventStreamResolver
{
    public string Namespace => ComputeConstants.Namespace;

    public bool TryResolve(ResourceId id, out string streamId)
    {
        var canonical = (id with { Action = null, DataPlaneSegment = null, DataPlanePath = null }).Canonical;
        streamId = ComputeConstants.StreamId(canonical);
        return true;
    }
}
