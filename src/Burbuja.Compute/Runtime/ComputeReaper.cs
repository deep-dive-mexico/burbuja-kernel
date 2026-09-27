using System.Text.Json.Nodes;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Recovery;
using Microsoft.Extensions.Options;

namespace Burbuja.Compute.Runtime;

/// <summary>
/// C7 consumer: sweeps running workspaces whose TTL has expired and deallocates them. The
/// recovery query is the index-backed candidate list.
/// </summary>
public sealed class ComputeReaper(
    ComputeStateStore states,
    IOutbox outbox,
    IClock clock,
    IOptions<ComputeOptions> options) : IStreamRecoveryHandler
{
    public string StreamType => ComputeConstants.StreamType;

    public RecoveryQuery? Query => new(
        ComputeConstants.Namespace,
        "workspaces",
        [nameof(ComputeWorkspaceState.Running)],
        200);

    public async ValueTask RecoverAsync(string streamId, CancellationToken ct)
    {
        var env = await states.GetAsync(streamId.ToLowerInvariant(), ct);
        if (env is null || env.Properties.State != ComputeWorkspaceState.Running)
            return;

        var now = clock.UtcNow;
        var idle = env.Properties.LastActivityAt is { } last ? now - last : TimeSpan.Zero;
        var idleLimit = env.Properties.IdleTimeout ?? options.Value.DefaultIdleTimeout;
        var expired = env.Properties.ExpiresAt is { } expires && expires <= now;
        if (!expired && idle < idleLimit)
            return;

        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), ComputeConstants.TopicLifecycle, new JsonObject
        {
            ["workspaceKey"] = streamId.ToLowerInvariant(),
            ["op"] = "deallocate",
        }), ct);
        _ = outbox;
    }
}
