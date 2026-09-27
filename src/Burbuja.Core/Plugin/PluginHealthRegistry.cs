using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Plugin;

namespace Burbuja.Core.Plugin;

/// <summary>The supervisor's view of one loaded plugin.</summary>
public enum PluginHealthState
{
    /// <summary>Loaded and wired (ConfigureServices ran); its services have not started yet.</summary>
    Registered,

    /// <summary>Every owned hosted service that has been started came up.</summary>
    Started,

    /// <summary>A hosted service threw out of <c>StartAsync</c>.</summary>
    StartFailed,

    /// <summary>A <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> faulted after starting.</summary>
    Faulted,

    /// <summary>The host stopped the plugin's services.</summary>
    Stopped,
}

/// <summary>
/// What the kernel knows about one loaded plugin, as a shape the Host can serialize. This is the
/// record half of the registry: the registry mutates, a snapshot never does.
/// </summary>
public sealed record PluginHealthEntrySnapshot(
    string Id,
    string Name,
    string Version,
    PluginLoadKind Load,
    bool Trusted,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> HostedServices,
    PluginHealthState State,
    PluginHealthStatus Status,
    string? Description,
    JsonObject? Data,
    DateTimeOffset? StartedAt,
    string? Error);

/// <summary>Every loaded plugin plus the worst status among them.</summary>
public sealed record PluginHealthSnapshot(
    PluginHealthStatus Status,
    IReadOnlyList<PluginHealthEntrySnapshot> Plugins);

/// <summary>
/// Track 3a: per-plugin health. The kernel keeps one entry per loaded plugin (manifest facts, the
/// hosted services it owns, when they started, the last error, and a supervisor state), the
/// supervised wrappers write lifecycle transitions into it, <see cref="PluginHealthCheck"/> writes
/// the folded health (registry state + contributors) into it, and the Host serializes
/// <see cref="Snapshot"/> for <c>/health/plugins</c>.
/// <para>
/// Every mutation is idempotent and cheap; the lock is only held for a dictionary lookup and a
/// field write, never across a plugin call, so a slow contributor can never block the supervisor.
/// </para>
/// </summary>
public sealed class PluginHealthRegistry(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Load order; the snapshot lists plugins the way the host booted them.</summary>
    private readonly List<string> _order = [];

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public IReadOnlyList<string> PluginIds
    {
        get
        {
            lock (_gate)
                return _order.ToArray();
        }
    }

    /// <summary>Records a loaded plugin and the hosted-service type names its own registrations produced.</summary>
    public void Register(PluginManifest manifest, IEnumerable<string>? hostedServices = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_gate)
        {
            if (!_entries.TryGetValue(manifest.Id, out var entry))
            {
                entry = new Entry(manifest);
                _entries[manifest.Id] = entry;
                _order.Add(manifest.Id);
            }

            entry.Manifest = manifest;
            if (hostedServices is not null)
            {
                foreach (var name in hostedServices)
                    entry.NoteService(name);
            }

            // The plugin is live as soon as it is loaded and wired; its owned services start right
            // after (RecordStarted stamps this too, for an entry that arrived without a registration).
            entry.StartedAt ??= _clock.GetUtcNow();
        }
    }

    /// <summary>Notes the concrete type of an owned hosted service (known for sure once it is constructed).</summary>
    public void NoteHostedService(string pluginId, string typeName)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(pluginId, out var entry))
                entry.NoteService(typeName);
        }
    }

    public void RecordStarted(string pluginId, string typeName)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pluginId, out var entry))
                return;

            entry.NoteService(typeName);
            entry.StartedAt ??= _clock.GetUtcNow();
            // A failure is sticky: one service coming up late never erases a sibling's failure.
            if (entry.State is PluginHealthState.Faulted or PluginHealthState.StartFailed)
                return;
            entry.State = PluginHealthState.Started;
        }
    }

    public void RecordStartFailed(string pluginId, string typeName, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            if (!_entries.TryGetValue(pluginId, out var entry))
                return;
            entry.NoteService(typeName);
            entry.State = PluginHealthState.StartFailed;
            entry.LastError = Describe(error);
        }
    }

    public void RecordFaulted(string pluginId, string typeName, Exception? error)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pluginId, out var entry))
                return;
            entry.NoteService(typeName);
            entry.State = PluginHealthState.Faulted;
            if (error is not null)
                entry.LastError = Describe(error);
        }
    }

    public void RecordStopped(string pluginId, string typeName)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pluginId, out var entry))
                return;
            entry.NoteService(typeName);
            if (entry.State is PluginHealthState.Started)
                entry.State = PluginHealthState.Stopped;
        }
    }

    /// <summary>The health check's folded verdict (registry state + every contributor).</summary>
    public void RecordHealth(string pluginId, PluginHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        lock (_gate)
        {
            if (_entries.TryGetValue(pluginId, out var entry))
                entry.Health = health;
        }
    }

    /// <summary>
    /// The registry's own opinion, before any contributor is asked: the supervisor state mapped to
    /// a health status. Only a failure or a fault is Unhealthy — a plugin that owns no hosted
    /// service is perfectly healthy at <see cref="PluginHealthState.Registered"/>.
    /// </summary>
    public PluginHealth StateHealth(string pluginId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(pluginId, out var entry)
                ? StateHealthLocked(entry)
                : new PluginHealth(PluginHealthStatus.Unhealthy, $"plugin '{pluginId}' is not registered");
        }
    }

    /// <summary>The whole registry as an immutable document shape.</summary>
    public PluginHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var plugins = new List<PluginHealthEntrySnapshot>(_order.Count);
            var worst = PluginHealthStatus.Healthy;
            foreach (var id in _order)
            {
                var entry = _entries[id];
                // The recorded verdict is the health check's fold; the state is recomputed here so a
                // fault that landed after the last probe is never under-reported.
                var health = entry.Health is null
                    ? StateHealthLocked(entry)
                    : Worse(entry.Health, StateHealthLocked(entry));
                if (health.Status > worst)
                    worst = health.Status;
                plugins.Add(new PluginHealthEntrySnapshot(
                    entry.Manifest.Id,
                    entry.Manifest.Name,
                    entry.Manifest.Version,
                    entry.Manifest.Load,
                    entry.Manifest.Trusted,
                    entry.Manifest.Provides.ToArray(),
                    entry.Manifest.Requires.Select(r => r.ToString()).ToArray(),
                    entry.Services.ToArray(),
                    entry.State,
                    health.Status,
                    health.Description,
                    health.Data,
                    entry.StartedAt,
                    entry.LastError));
            }

            return new PluginHealthSnapshot(worst, plugins);
        }
    }

    private static PluginHealth StateHealthLocked(Entry entry) => entry.State switch
    {
        PluginHealthState.StartFailed => new PluginHealth(
            PluginHealthStatus.Unhealthy,
            $"a hosted service failed to start: {entry.LastError ?? "unknown error"}"),
        PluginHealthState.Faulted => new PluginHealth(
            PluginHealthStatus.Unhealthy,
            $"a hosted service faulted: {entry.LastError ?? "unknown error"}"),
        PluginHealthState.Stopped => new PluginHealth(PluginHealthStatus.Degraded, "hosted services are stopped"),
        PluginHealthState.Started => new PluginHealth(
            PluginHealthStatus.Healthy,
            $"{entry.Services.Count} hosted service(s) started"),
        _ when entry.Services.Count > 0 => new PluginHealth(
            PluginHealthStatus.Degraded,
            $"{entry.Services.Count} hosted service(s) registered but not started"),
        _ => new PluginHealth(PluginHealthStatus.Healthy, "no hosted services"),
    };

    private static PluginHealth Worse(PluginHealth a, PluginHealth b) => b.Status > a.Status ? b : a;

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

    private sealed class Entry(PluginManifest manifest)
    {
        public PluginManifest Manifest { get; set; } = manifest;

        public List<string> Services { get; } = [];

        public PluginHealthState State { get; set; } = PluginHealthState.Registered;

        public DateTimeOffset? StartedAt { get; set; }

        public string? LastError { get; set; }

        public PluginHealth? Health { get; set; }

        public void NoteService(string typeName)
        {
            if (string.IsNullOrEmpty(typeName) || Services.Contains(typeName, StringComparer.Ordinal))
                return;
            Services.Add(typeName);
        }
    }
}
