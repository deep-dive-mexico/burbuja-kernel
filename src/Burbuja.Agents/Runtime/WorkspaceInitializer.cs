using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Handlers;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// W1 — the workspace initializer: it binds a workspace's defaults and closes the operation the PUT opened.
///
/// <para><b>Why a worker and not the front door.</b> Provisioning a workspace means creating another resource
/// in another provider — a <c>Burbuja.Compute/workspaces</c> bound to this workspace — and that is an ARM call
/// with its own policy decision, its own operation and its own lifecycle. Doing it inline would hold the
/// caller's PUT open for as long as the other provider takes, which is exactly what the 202 + operation shape
/// exists to avoid.</para>
///
/// <para><b>It runs as its own principal.</b> The calls travel through the same front door as everybody
/// else's, so they need a key: <c>Agents:LifecycleKey</c>, resolved against the host's own auth list. A host
/// with no such key refuses the initialize with that sentence rather than crashing from deep inside the
/// pipeline — the lesson A2's pipeline key taught.</para>
///
/// <para><b>Idempotent by the resource, not by a flag.</b> The operation carries the generation it was started
/// for; a message whose workspace has moved on (a newer initialize, a delete) is dropped. Re-running an
/// initialize for a workspace that is already ready is answered by the handler, not here.</para>
/// </summary>
public sealed class WorkspaceInitializer(
    IMessageBus bus,
    IResourceStore store,
    IOperationTracker ops,
    IEventLog events,
    IArmClient arm,
    IClock clock,
    IConfiguration configuration,
    ILogger<WorkspaceInitializer> log) : IHostedService
{
    /// <summary>The setting naming the key the initializer's own ARM calls run as.</summary>
    public const string LifecycleKeySetting = "Agents:LifecycleKey";

    /// <summary>
    /// How long the initializer waits for the compute workspace to leave <c>creating</c> before it reports the
    /// state it last saw. Bounded because an initialize that never completes must not hold an operation open
    /// forever: a workspace whose compute is still provisioning is a fact a reader can see, and the operation
    /// says so instead of pretending.
    /// </summary>
    public static readonly TimeSpan ComputeWait = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    private IAsyncDisposable? _sub;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var role = configuration["Kernel:Role"] ?? "all";
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sub = await bus.SubscribeAsync(AgentsConstants.TopicWorkspaceLifecycle, OnMessage, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(key))
            return;

        // M13 R0 — ONE BAD MESSAGE MUST NOT DISABLE PROVISIONING FOR THE PROCESS.
        //
        // <para>Measured on the browser lane (2026-09-16): a single `ArmException: The provided ETag does not
        // match the current resource.` escaped this method, `InMemoryMessageBus` logged "bus subscriber for
        // topic 'agents.workspace.lifecycle' failed and stopped" and STOPPED the pump — so every workspace
        // provisioned after it stayed `Provisioning` forever, with no error on the resource and no second
        // message. Two browser facts went red for it (`G25`, `G26`) and the failure read as a slow host.
        // A subscriber outlives the messages it handles, so its per-message work catches its own failures and
        // says so; the resource keeps its own state (`Failed` + `LifecycleError` when the initializer could
        // write it) and the log carries the rest.</para>
        try
        {
            await RunAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "workspace initialize failed for '{Key}'; the subscriber stays up", key);
        }
    }

    /// <summary>Runs one initialize. Public so a fact can drive it without a bus.</summary>
    public async ValueTask RunAsync(string workspaceKey, CancellationToken ct)
    {
        var raw = await store.GetAsync(workspaceKey, ct).ConfigureAwait(false);
        if (raw is null)
            return;
        var properties = raw.Properties.Deserialize<WorkspaceProperties>(JsonWire.Options);
        if (properties is null || properties.Lifecycle != WorkspaceHandler.Lifecycle.Provisioning)
            return;

        var operationId = properties.OperationId ?? "";
        var computeId = properties.ComputeWorkspaceId ?? WorkspaceHandler.DefaultComputeId(IdOf(raw));
        try
        {
            var key = configuration[LifecycleKeySetting];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    $"No '{LifecycleKeySetting}' is configured, so the workspace initializer has no principal to "
                    + "provision with. Set it to an api key the host's auth list knows (Owner), or initialize the "
                    + "workspace by hand.");
            }

            await BindComputeAsync(raw, computeId, key, ct).ConfigureAwait(false);

            properties.Lifecycle = WorkspaceHandler.Lifecycle.Ready;
            properties.LifecycleError = null;
            properties.ComputeWorkspaceId = computeId;
            var stored = await PersistAsync(raw, properties, operationId, ProvisioningState.Succeeded, ct).ConfigureAwait(false);
            // A null means the run was SUPERSEDED while it was binding — the workspace carries a newer operation
            // — so nothing is written and nothing is closed. See `PersistAsync`.
            if (stored is null)
                return;

            await ops.SucceedAsync(operationId, new JsonObject
            {
                ["workspace"] = stored.Id,
                ["computeWorkspaceId"] = computeId,
                ["generation"] = properties.Generation,
            }, ct).ConfigureAwait(false);
            await AppendAsync(stored, WorkspaceHandler.ReadyEvent, "ready", properties.Generation, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            properties.Lifecycle = WorkspaceHandler.Lifecycle.Failed;
            properties.LifecycleError = ex.Message;
            var stored = await PersistAsync(raw, properties, operationId, ProvisioningState.Failed, ct).ConfigureAwait(false);
            if (stored is null)
                return;

            await ops.FailAsync(operationId, ArmError.Validation($"WorkspaceInitializeFailed: {ex.Message}", []), ct)
                .ConfigureAwait(false);
            await AppendAsync(stored, WorkspaceHandler.FailedEvent, ex.Message, properties.Generation, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Create (or reconcile) the compute workspace this workspace runs on, and wait until it is not creating.
    ///
    /// <para>The body binds the two resources to each other: <c>repo.agentsWorkspaceId</c> is the agents
    /// workspace, which is what makes a job's worktree live under the COMPUTE workspace's root (the binding
    /// M8 named and nothing had written).</para>
    /// </summary>
    private async ValueTask BindComputeAsync(
        ResourceEnvelope<JsonObject> workspace,
        string computeId,
        string key,
        CancellationToken ct)
    {
        var existing = await arm.SendAsync(new ArmClientRequest("GET", computeId, Headers: Key(key)), ct).ConfigureAwait(false);
        if (existing.StatusCode is >= 400 and not 404)
        {
            throw new InvalidOperationException(
                $"Reading '{computeId}' answered {existing.StatusCode}: {existing.Error?.Message ?? "no message"}");
        }

        if (existing.StatusCode == 404)
        {
            var body = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["displayName"] = workspace.Name,
                    ["backend"] = "local-directory",
                    ["image"] = "ubuntu-24.04",
                    ["size"] = "small",
                    ["repo"] = new JsonObject { ["agentsWorkspaceId"] = workspace.Id },
                },
            };
            var created = await arm.SendAsync(
                new ArmClientRequest("PUT", computeId, Headers: Key(key), Body: body), ct).ConfigureAwait(false);
            if (created.StatusCode is < 200 or > 299)
            {
                throw new InvalidOperationException(
                    $"Creating '{computeId}' answered {created.StatusCode}: {created.Error?.Message ?? "no message"}");
            }
        }

        var deadline = clock.UtcNow + ComputeWait;
        while (clock.UtcNow < deadline)
        {
            var read = await arm.SendAsync(new ArmClientRequest("GET", computeId, Headers: Key(key)), ct).ConfigureAwait(false);
            var state = read.Body?["properties"]?["state"]?.GetValue<string>() ?? "";
            var provisioning = read.Body?["properties"]?["provisioningState"]?.GetValue<string>()
                               ?? read.Body?["provisioningState"]?.GetValue<string>() ?? "";
            if (state.Equals("running", StringComparison.OrdinalIgnoreCase)
                || provisioning.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (state.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || provisioning.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The compute workspace '{computeId}' failed to provision.");
            }

            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }

        // The bound was reached: the compute workspace is still coming up, and the workspace is READY as far as
        // this host is concerned — the binding exists and the reader can see the state it is in. Reporting a
        // failure here would be wrong (nothing failed) and waiting forever would hold the operation open.
    }

    private static IReadOnlyDictionary<string, string> Key(string key) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = key };

    /// <summary>
    /// Writes what an initialize produced, or NOTHING when the run it belongs to has been superseded.
    ///
    /// <para><b>M13 R0 — the write RE-READS, and this is the fact for a red browser lane.</b> The first version
    /// wrote with the ETag it had read before <c>BindComputeAsync</c>, which can take up to <c>ComputeWait</c>
    /// (20 s) while the front door keeps serving the same resource. Any write in that window made this one a
    /// 412 — and a 412 here was not a lost update to shrug at: it escaped <c>OnMessage</c>, killed the
    /// lifecycle subscriber, and left every workspace after it stuck in <c>Provisioning</c> for the life of the
    /// process (see the note there).</para>
    ///
    /// <para><b>The rule, and the bug the first attempt at it had.</b> The comparison has to be between the
    /// STORED operation and the operation THIS RUN opened — not between the stored operation and the object
    /// this run is about to write. The first attempt compared the latter, which is the same value on both sides
    /// by construction, so the guard passed for exactly the case it existed to catch: a superseded run wrote
    /// <c>Ready</c> over a workspace whose newer initialize had not started, closed ANOTHER run's operation, and
    /// the unit fact for it failed with the resource already <c>Ready</c> when it should still have been
    /// provisioning. <c>claims</c> is the run's own operation id, read before the bind.</para>
    ///
    /// <para>Returns null when the write was dropped, and every caller returns on null rather than closing an
    /// operation nobody is waiting for.</para>
    /// </summary>
    private async ValueTask<ResourceEnvelope<JsonObject>?> PersistAsync(
        ResourceEnvelope<JsonObject> raw,
        WorkspaceProperties properties,
        string claims,
        ProvisioningState state,
        CancellationToken ct)
    {
        var current = await store.GetAsync(raw.Id.ToLowerInvariant(), ct).ConfigureAwait(false);
        if (current is null)
            return null;

        var currentProperties = current.Properties.Deserialize<WorkspaceProperties>(JsonWire.Options);
        var stillOurs = currentProperties is not null
                        && currentProperties.Lifecycle == WorkspaceHandler.Lifecycle.Provisioning
                        && string.Equals(currentProperties.OperationId, claims, StringComparison.Ordinal);
        if (!stillOurs)
            return null;

        var now = clock.UtcNow;
        var envelope = current with
        {
            Properties = System.Text.Json.JsonSerializer.SerializeToNode(properties, JsonWire.Options)!.AsObject(),
            ProvisioningState = state,
            SystemData = current.SystemData with { LastModifiedAt = now, LastModifiedBy = "workspace-initializer" },
        };
        return await store.UpsertAsync(envelope, envelope.ETag, ct).ConfigureAwait(false);
    }

    private async ValueTask AppendAsync(
        ResourceEnvelope<JsonObject> envelope,
        string type,
        string summary,
        int generation,
        CancellationToken ct) =>
        await events.AppendAsync(new EventAppend(
            AgentsConstants.StreamId(envelope.Id),
            null,
            type,
            summary,
            new JsonObject
            {
                ["workspace"] = envelope.Id,
                ["computeWorkspaceId"] = envelope.Properties["computeWorkspaceId"]?.GetValue<string>(),
                ["generation"] = generation,
            },
            RenderLane.Log,
            envelope.Name,
            null,
            AgentsConstants.Namespace), ct).ConfigureAwait(false);

    /// <summary>The resource id of a stored workspace envelope, for the default compute binding.</summary>
    private static ResourceId IdOf(ResourceEnvelope<JsonObject> envelope)
    {
        var id = envelope.Id;
        return new ResourceId
        {
            SubscriptionId = Segment(id, "subscriptions"),
            ResourceGroup = Segment(id, "resourceGroups") is { Length: > 0 } rg ? rg : null,
            Namespace = AgentsConstants.Namespace,
            TypeSegments = ["workspaces"],
            NameSegments = [envelope.Name],
        };
    }

    private static string Segment(string id, string marker)
    {
        var at = id.IndexOf($"/{marker}/", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return "";
        var rest = id[(at + marker.Length + 2)..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..slash];
    }
}
