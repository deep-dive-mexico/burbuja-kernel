using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Channel;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Host;

/// <summary>
/// M7.1d/e: collects host facts on a timer, puts them on the event log for the monitor lane, and
/// raises a notice when a threshold is crossed.
///
/// <para>Thresholds are <b>edge-triggered</b>: a notice is raised when a condition becomes true and
/// re-armed when it clears, so a Mac that sits at 8 GB free for a week is one message, not ten
/// thousand. That state is why the suppression lives here and not in the notifier — this is the only
/// component that knows what the condition was a minute ago.</para>
///
/// <para>It runs in the control plane and in <c>all</c>, because the facts are about the machine the
/// process is on and every host that serves the portal wants to draw them.</para>
/// </summary>
public sealed class HostFactsPublisher(
    IEnumerable<IHostFactsProvider> providers,
    IHostFactsSnapshot snapshot,
    IEventLog events,
    INotifier notifier,
    TimeProvider time,
    IOptions<RuntimeOptions> runtime,
    IOptions<KernelOptions> kernel,
    ILogger<HostFactsPublisher> log) : IHostedService
{
    /// <summary>The single stream every host-facts reading is appended to.</summary>
    public const string StreamId = "host.facts";

    public const string EventType = "host.facts";

    /**
     * At or below this, on battery, the human is told — the owner's number, and the boundary is
     * inclusive on purpose: 15 is the charge the owner asked to hear about, so 15% alerts and 16%
     * does not. (Disk and pressure below stay strictly-below: those constants are engineering floors
     * rather than a number a person named.)
     */
    public const decimal LowBatteryPercent = 15m;

    /** Below this the human is told; macOS itself starts complaining around here. */
    public const decimal LowDiskFreeGb = 10m;

    /** `kern.memorystatus_level` below this is memory pressure, not free memory. */
    public const decimal LowMemoryPressure = 20m;

    private const string BatteryKey = "host.battery.low";
    private const string DiskKey = "host.disk.low";
    private const string PressureKey = "host.memory.pressure";

    private readonly Dictionary<string, bool> _raised = new(StringComparer.Ordinal);

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

        if (!providers.Any())
        {
            // No provider staged (a Linux host today, or a host with no HostFacts plugin): say so once
            // rather than starting a loop that can never produce anything.
            log.LogInformation("no IHostFactsProvider is registered; host facts will not be published");
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
                var interval = runtime.Value.HostFactsInterval;
                if (interval <= TimeSpan.Zero)
                    interval = TimeSpan.FromSeconds(60);
                // The first collection happens after one interval on purpose: a boot is busy, and
                // shelling out ten times while the host is still starting is the least useful moment.
                await Task.Delay(interval, time, ct).ConfigureAwait(false);
                await CollectOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "host facts collection failed");
            }
        }
    }

    /// <summary>One collection, publish and threshold evaluation. Public so a test can drive it.</summary>
    public async ValueTask<HostFacts?> CollectOnceAsync(CancellationToken ct)
    {
        foreach (var provider in providers)
        {
            HostFacts? facts;
            try
            {
                facts = await provider.CollectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "host facts provider '{Platform}' failed", provider.Platform);
                continue;
            }

            if (facts is null)
                continue;

            snapshot.Publish(facts);
            await events.AppendAsync(new EventAppend(
                StreamId,
                provider.Platform,
                EventType,
                "collected",
                JsonSerializer.SerializeToNode(facts, JsonWire.Options)!.AsObject(),
                RenderLane.Monitor,
                provider.Platform,
                null,
                "host.facts"), ct).ConfigureAwait(false);

            await EvaluateThresholdsAsync(facts, ct).ConfigureAwait(false);
            return facts;
        }

        return null;
    }

    private async ValueTask EvaluateThresholdsAsync(HostFacts facts, CancellationToken ct)
    {
        await RaiseAsync(
            BatteryKey,
            facts.Battery.Percent is { } percent && facts.Battery.Charging == false && percent <= LowBatteryPercent,
            NoticeKind.Alert,
            $"🔋 Batería al {facts.Battery.Percent:0}% y sin cargador" + EstimateSuffix(facts.Battery) + ".",
            ct).ConfigureAwait(false);

        await RaiseAsync(
            DiskKey,
            facts.DiskFreeGb is { } free && free < LowDiskFreeGb,
            NoticeKind.Alert,
            $"💾 Quedan {facts.DiskFreeGb:0} GB libres en el disco.",
            ct).ConfigureAwait(false);

        await RaiseAsync(
            PressureKey,
            facts.MemoryPressureLevel is { } pressure && pressure < LowMemoryPressure,
            NoticeKind.Warn,
            $"🧠 Presión de memoria al {facts.MemoryPressureLevel:0}%.",
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ` · quedan ~42 min` when the reading carried an estimate, nothing when it did not. The whole
    /// point of an alert is that a person has to do something about it, and how long they have is the
    /// half of that they cannot work out from the percentage.
    /// </summary>
    private static string EstimateSuffix(BatteryFacts battery) =>
        battery.EstimateMinutes is { } minutes
            ? $" · quedan ~{(minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min")}"
            : "";

    /// <summary>
    /// Notify on the crossing, not on the state: raise once when the condition becomes true, re-arm
    /// silently when it clears. A condition that stays true is one message.
    /// </summary>
    private async ValueTask RaiseAsync(string key, bool active, NoticeKind kind, string text, CancellationToken ct)
    {
        var was = _raised.TryGetValue(key, out var previous) && previous;
        _raised[key] = active;
        if (!active || was)
            return;

        await notifier.NotifyAsync(new Notice(kind, text, At: time.GetUtcNow()), ct).ConfigureAwait(false);
    }
}
