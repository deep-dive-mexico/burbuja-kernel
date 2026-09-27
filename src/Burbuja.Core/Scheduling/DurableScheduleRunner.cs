using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Scheduling;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Scheduling;

/// <summary>
/// M7.1: the durable scheduler. Every tick it lists each registered schedule resource, asks its
/// <see cref="IScheduleSource"/> when the next window is due, and fires the ones that have come.
///
/// <para>Durable means the state that decides firing lives in the store, not in a timer: after a
/// restart, <c>next = Cron.Next(LastFiredAt ?? CreatedAt)</c> recomputes exactly the same answer, so
/// a missed window fires once and a window that already fired does not fire again. That is also why
/// this is a sweeper rather than a long-lived timer per schedule — there is no timer to lose.</para>
///
/// <para>Two ticks in the same window cannot both fire: the first claims
/// <c>sched:{id}:{next:O}</c> in the cache, and the claim is per-window rather than per-tick so a
/// restart inside the same minute does not double-fire. Two <i>nodes</i> in the same window are
/// separated by the ETag CAS in <see cref="IScheduleSource.MarkFiredAsync"/>, and the loser writes
/// <c>schedule.skipped_etag</c> rather than treating it as a failure.</para>
/// </summary>
public sealed class DurableScheduleRunner(
    IEnumerable<IScheduleSource> sources,
    IResourceStore store,
    ICache cache,
    IEventLog events,
    TimeProvider time,
    IOptions<RuntimeOptions> runtime,
    IOptions<KernelOptions> kernel,
    ILogger<DurableScheduleRunner> log) : IHostedService
{
    private const int PageSize = 200;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var role = kernel.Value.Role;
        if (!role.Equals("control-plane", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Loop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var interval = runtime.Value.ScheduleTickInterval;
                if (interval <= TimeSpan.Zero)
                    interval = TimeSpan.FromSeconds(30);
                await Task.Delay(interval, time, ct).ConfigureAwait(false);
                await SweepOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not stop the scheduler for the rest of the process lifetime.
                log.LogError(ex, "schedule sweep failed");
            }
        }
    }

    /// <summary>One pass over every registered source. Public so a test can drive ticks by hand.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        foreach (var source in sources)
        {
            PagedResult<ResourceEnvelope<JsonObject>> page;
            try
            {
                page = await store.ListAsync(
                    new ResourceStoreQuery(null, null, source.Namespace, source.Type, PageSize, null, null, null),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "listing schedules for {Namespace}/{Type} failed", source.Namespace, source.Type);
                continue;
            }

            foreach (var envelope in page.Value)
                await ConsiderAsync(source, envelope, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ConsiderAsync(IScheduleSource source, ResourceEnvelope<JsonObject> envelope, CancellationToken ct)
    {
        ScheduleDefinition? definition;
        try
        {
            definition = source.Read(envelope);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "reading schedule {Key} failed", envelope.Id);
            return;
        }

        if (definition is null || definition.Paused)
            return;

        if (!CronExpression.TryParse(definition.Cron, out var cron) || cron is null)
        {
            log.LogWarning("schedule {Key} has an unparseable cron '{Cron}'; skipping it", envelope.Id, definition.Cron);
            return;
        }

        var now = time.GetUtcNow();
        // The created time is the honest anchor for a schedule that has never fired; a store that did
        // not record one falls back to "now", i.e. start counting from when we first saw it.
        DateTimeOffset anchor = definition.LastFiredAt ?? envelope.SystemData.CreatedAt ?? now;
        var due = cron.Next(anchor, definition.TimeZone);
        if (due is null || due > now)
            return;

        // Claim the window before doing anything. `next` is a minute-resolution instant, so a second
        // tick inside the same window (or a restart inside it) finds the claim and does nothing.
        var claim = $"sched:{envelope.Id}:{due.Value:O}";
        if (!await cache.TryAddAsync(claim, true, TimeSpan.FromHours(24), ct).ConfigureAwait(false))
            return;

        try
        {
            await source.FireAsync(envelope, due.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Release the claim: the fire did not happen, so the window is still owed. Without this a
            // transient failure would silently skip a window until the claim aged out.
            await cache.SetAsync<bool?>(claim, null, TimeSpan.Zero, ct).ConfigureAwait(false);
            log.LogError(ex, "firing schedule {Key} for {Due} failed", envelope.Id, due.Value);
            return;
        }

        var next = cron.Next(now, definition.TimeZone);
        var marked = await source.MarkFiredAsync(envelope, now, next, ct).ConfigureAwait(false);

        var streamId = ScheduleEvents.StreamId(envelope.Id);
        var payload = new JsonObject
        {
            ["scheduledFor"] = due.Value.ToString("O"),
            ["firedAt"] = now.ToString("O"),
            ["nextRunAt"] = next?.ToString("O"),
            ["claimedByThisNode"] = marked,
        };

        await events.AppendAsync(new EventAppend(
            streamId,
            envelope.Name,
            marked ? ScheduleEvents.Fired : ScheduleEvents.SkippedETag,
            marked ? "fired" : "skipped (etag)",
            payload,
            RenderLane.Log,
            envelope.Name,
            null,
            source.Type), ct).ConfigureAwait(false);

        if (!marked)
            log.LogInformation("schedule {Key} window {Due} was marked by another node", envelope.Id, due.Value);
    }
}
