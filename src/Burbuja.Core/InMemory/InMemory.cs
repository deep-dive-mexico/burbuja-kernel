using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Events;
using Burbuja.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.InMemory;

public sealed class BusFaultInjector
{
    public int Seed { get; set; } = 1;
    public double DropProbability { get; set; }
    public double DuplicateProbability { get; set; }
    public double ReorderProbability { get; set; }
    public TimeSpan Delay { get; set; }

    public Random CreateRandom() => new(Seed);
}

public sealed class InMemoryMessageBus(TimeProvider time, BusFaultInjector? faults = null, ILogger<InMemoryMessageBus>? log = null) : IMessageBus
{
    private sealed record Sub(string Topic, Channel<BusMessage> Channel, CancellationTokenSource Cts, Task Pump);

    private readonly ConcurrentDictionary<Guid, Sub> _subs = [];
    private readonly BusFaultInjector _faults = faults ?? new();

    public async ValueTask PublishAsync(BusMessage message, CancellationToken ct)
    {
        var rng = _faults.CreateRandom();
        if (_faults.DropProbability > 0 && rng.NextDouble() < _faults.DropProbability)
            return;
        if (_faults.Delay > TimeSpan.Zero)
            await Task.Delay(_faults.Delay, time, ct);

        var targets = _subs.Values.Where(s => s.Topic.Equals(message.Topic, StringComparison.Ordinal)).ToArray();
        if (_faults.ReorderProbability > 0 && targets.Length > 1 && rng.NextDouble() < _faults.ReorderProbability)
            targets = targets.Reverse().ToArray();

        foreach (var sub in targets)
            await sub.Channel.Writer.WriteAsync(Clone(message), ct);

        if (_faults.DuplicateProbability > 0 && rng.NextDouble() < _faults.DuplicateProbability)
        {
            foreach (var sub in targets)
                await sub.Channel.Writer.WriteAsync(Clone(message), ct);
        }
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(string topic, Func<BusMessage, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        // Qualified for the same reason as the tailer below: Burbuja.Core.Channel shadows
        // System.Threading.Channels.Channel in this namespace.
        var ch = System.Threading.Channels.Channel.CreateBounded<BusMessage>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in ch.Reader.ReadAllAsync(cts.Token))
                    await handler(msg, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // unsubscribed
            }
            catch (Exception ex)
            {
                // A throwing handler used to kill the pump with an unobserved exception, so the
                // subscriber went quiet for the rest of the process lifetime. Log it loudly; the
                // pump still stops, but the failure is no longer invisible.
                log?.LogError(ex, "bus subscriber for topic '{Topic}' failed and stopped", topic);
            }
        }, cts.Token);
        _subs[id] = new Sub(topic, ch, cts, pump);
        return new(new Unsubscriber(this, id));
    }

    private void Unsubscribe(Guid id)
    {
        if (!_subs.TryRemove(id, out var sub))
            return;
        sub.Channel.Writer.TryComplete();
        sub.Cts.Cancel();
    }

    private static BusMessage Clone(BusMessage m) =>
        new(m.MessageId, m.Topic, m.Payload.DeepClone(), m.Headers);

    private sealed class Unsubscriber(InMemoryMessageBus bus, Guid id) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            bus.Unsubscribe(id);
            return default;
        }
    }
}

public sealed class InMemoryCache(IClock clock) : Burbuja.Core.Abstractions.Store.ICache
{
    private readonly ConcurrentDictionary<string, (object Value, DateTimeOffset? Expires)> _data = new(StringComparer.Ordinal);

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        if (_data.TryGetValue(key, out var item) && (item.Expires is null || item.Expires > clock.UtcNow))
            return new((T?)item.Value);
        _data.TryRemove(key, out _);
        return new(default(T?));
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct)
    {
        DateTimeOffset? exp = ttl is null ? null : clock.UtcNow + ttl;
        _data[key] = (value!, exp);
        return default;
    }

    public ValueTask<bool> TryAddAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct)
    {
        DateTimeOffset? exp = ttl is null ? null : clock.UtcNow + ttl;
        return new(_data.TryAdd(key, (value!, exp)));
    }
}

public sealed class InMemoryDecisionCache<T> : IDecisionCache<T>
{
    private readonly ConcurrentDictionary<string, T> _data = new(StringComparer.Ordinal);

    public ValueTask<T?> GetAsync(string streamId, string decisionKey, CancellationToken ct)
    {
        _data.TryGetValue(Key(streamId, decisionKey), out var value);
        return new(value);
    }

    public ValueTask SetAsync(string streamId, string decisionKey, T value, CancellationToken ct)
    {
        _data[Key(streamId, decisionKey)] = value;
        return default;
    }

    private static string Key(string streamId, string decisionKey) => streamId + "\u001f" + decisionKey;
}

public sealed class InMemoryEventLog(
    IRedactor redactor,
    IClock clock,
    int tailerCapacity = 256,
    IArtifactStore? spill = null,
    int maxPayloadBytes = EventPayloadCap.DefaultMaxBytes) : IEventLog
{
    private sealed class Tailer
    {
        public required Channel<LedgerEvent> Channel { get; init; }
        public int Dropped;
        public LedgerEvent? LastDropped;
    }

    internal int DebugTailerCount => _tailers.Count;
    internal int DebugDropped => _tailers.Values.Sum(t => Volatile.Read(ref t.Dropped));
    internal int DebugBuffered => _tailers.Values.Sum(t => t.Channel.Reader.Count);

    internal void AddHungTailer(int capacity)
    {
        // Qualified: this file sits in Burbuja.Core.InMemory, and the kernel's own
        // Burbuja.Core.Channel namespace (M7.1e) shadows System.Threading.Channels.Channel here.
        var ch = System.Threading.Channels.Channel.CreateBounded<LedgerEvent>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _tailers[Guid.NewGuid()] = new Tailer { Channel = ch };
    }

    private long _seq;
    private readonly List<LedgerEvent> _all = [];
    private readonly ConcurrentDictionary<string, List<LedgerEvent>> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Tailer> _tailers = [];

    public async ValueTask<long> AppendAsync(EventAppend evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var summary = redactor.Redact(evt.Summary ?? "");
        if (summary.Length > 512)
            summary = summary[..512];
        var seq = Interlocked.Increment(ref _seq);
        JsonNode? payload = evt.Payload is JsonNode n ? RedactJson(n.DeepClone()) : null;
        if (payload is not null && spill is not null)
            payload = await EventPayloadCap.TrySpillAsync(payload, evt.StreamId, seq, maxPayloadBytes, spill, ct);
        var list = _streams.GetOrAdd(evt.StreamId, _ => []);
        LedgerEvent stored;
        // H1: the chain link is computed INSIDE the stream's own lock. Two writers appending to one stream
        // concurrently would otherwise both read the same predecessor and write two events claiming the
        // same position — a fork that verifies as a break on every later read, produced by the store
        // itself. The lock is what makes the sequence dense and the chain single.
        lock (list)
        {
            var (streamSeq, previousHash) = EventChain.Next(list.Count > 0 ? list[^1] : null);
            var chained = new LedgerEvent(
                seq,
                evt.StreamId,
                evt.SubStreamId,
                evt.Type,
                summary,
                payload,
                evt.Lanes,
                evt.Subject,
                evt.Value,
                evt.StreamType,
                clock.UtcNow,
                streamSeq,
                previousHash,
                null);
            stored = chained with { Hash = EventChain.HashOf(chained) };
            list.Add(stored);
        }

        lock (_all)
            _all.Add(stored);
        foreach (var tailer in _tailers.Values)
        {
            if (tailer.Channel.Writer.TryWrite(stored))
                continue;
            Interlocked.Increment(ref tailer.Dropped);
            tailer.LastDropped = stored;
            var lag = stored with
            {
                Type = "event.lag",
                Summary = "tailer overflow",
                Payload = null,
                Lanes = RenderLane.Monitor,
            };
            tailer.Channel.Writer.TryWrite(lag);
        }

        return seq;
    }

    private JsonNode RedactJson(JsonNode node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(redactor.Redact(s))!;
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var kv in o)
                    copy[kv.Key] = kv.Value is null ? null : RedactJson(kv.Value);
                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                foreach (var item in a)
                    copy.Add(item is null ? null : RedactJson(item));
                return copy;
            }
            default:
                return node.DeepClone();
        }
    }

    public IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct) =>
        Tail(e => e.Type == "event.lag" || (e.StreamId.Equals(streamId, StringComparison.Ordinal) && e.Seq > afterSeq), ct);

    public IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct) =>
        Tail(e => e.Type == "event.lag" || (e.Seq > afterSeq && (lanes == RenderLane.None || (e.Lanes & lanes) != 0)), ct);

    public IAsyncEnumerable<LedgerEvent> ReadRangeAsync(
        string streamId,
        long fromSeq,
        long toSeqExclusive,
        int limit,
        RenderLane? lanes,
        CancellationToken ct) =>
        ReadRange(streamId, fromSeq, toSeqExclusive, limit, lanes, ct);

    private async IAsyncEnumerable<LedgerEvent> ReadRange(
        string streamId,
        long fromSeq,
        long toSeqExclusive,
        int limit,
        RenderLane? lanes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!_streams.TryGetValue(streamId, out var list))
            yield break;

        List<LedgerEvent> page;
        lock (list)
        {
            // H1: the order is the CHAIN's, not the store's. The global `Seq` is handed out before the
            // stream lock is taken, so under concurrency two writers can take seq 5 and 6 and reach the
            // lock in the other order — which gives the event with the HIGHER global seq the LOWER stream
            // sequence. Ordering by `Seq` therefore returns a chain whose links look broken while the
            // chain itself is sound. `StreamSeq` is allocated inside the lock together with the link, so
            // it is the only ordering that describes the stream; `Seq` breaks ties for a store that has
            // not been migrated and carries 0 there.
            // (Found by the multi-writer fact, which failed two runs in three before this.)
            page = list
                .Where(e => e.Seq >= fromSeq && e.Seq < toSeqExclusive)
                .Where(e => lanes is null || lanes == RenderLane.None || (e.Lanes & lanes.Value) != 0)
                .OrderBy(e => e.StreamSeq)
                .ThenBy(e => e.Seq)
                .ToList();
        }

        if (limit > 0 && page.Count > limit)
            page.RemoveRange(limit, page.Count - limit);
        foreach (var e in page)
        {
            ct.ThrowIfCancellationRequested();
            yield return Clone(e);
        }
    }

    private async IAsyncEnumerable<LedgerEvent> Tail(
        Func<LedgerEvent, bool> predicate,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        // Qualified: this file sits in Burbuja.Core.InMemory, and the kernel's own
        // Burbuja.Core.Channel namespace (M7.1e) shadows System.Threading.Channels.Channel here.
        var ch = System.Threading.Channels.Channel.CreateBounded<LedgerEvent>(new BoundedChannelOptions(Math.Max(1, tailerCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        var tailer = new Tailer { Channel = ch };
        _tailers[id] = tailer;
        var seen = new HashSet<long>();
        try
        {
            List<LedgerEvent> snapshot;
            lock (_all)
                snapshot = _all.Where(predicate).ToList();
            foreach (var e in snapshot)
            {
                seen.Add(e.Seq);
                yield return Clone(e);
            }

            while (await ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (TryLag(tailer, out var lagFromWait))
                    yield return Clone(lagFromWait);

                while (ch.Reader.TryRead(out var evt))
                {
                    if (TryLag(tailer, out var lagFromRead))
                        yield return Clone(lagFromRead);

                    if (evt.Type == "event.lag")
                    {
                        yield return Clone(evt);
                        continue;
                    }

                    if (predicate(evt) && seen.Add(evt.Seq))
                        yield return Clone(evt);
                }
            }
        }
        finally
        {
            _tailers.TryRemove(id, out _);
            ch.Writer.TryComplete();
        }
    }

    // JsonNode is not thread-safe and tailers share the stored event: each consumer gets its own
    // deep clone so one tailer mutating a payload cannot corrupt the log or another tailer.
    private static LedgerEvent Clone(LedgerEvent evt) =>
        evt.Payload is null ? evt : evt with { Payload = evt.Payload.DeepClone() };

    private static bool TryLag(Tailer tailer, out LedgerEvent lag)
    {
        lag = null!;
        if (Volatile.Read(ref tailer.Dropped) <= 0)
            return false;
        var dropped = Interlocked.Exchange(ref tailer.Dropped, 0);
        if (dropped <= 0 || tailer.LastDropped is not { } last)
            return false;
        lag = last with
        {
            Type = "event.lag",
            Summary = $"tailer overflow dropped {dropped}",
            Payload = null,
            Lanes = RenderLane.Monitor,
        };
        return true;
    }
}

public sealed class InMemoryOutbox : Burbuja.Core.Abstractions.Outbox.IOutbox, Burbuja.Core.Abstractions.Outbox.IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, Burbuja.Core.Abstractions.Outbox.OutboxRecord> _items = [];

    public ValueTask EnqueueAsync(Burbuja.Core.Abstractions.Outbox.OutboxMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items[message.MessageId] = new Burbuja.Core.Abstractions.Outbox.OutboxRecord(
            message,
            Burbuja.Core.Abstractions.Outbox.OutboxStatus.Pending,
            0,
            DateTimeOffset.MinValue,
            null);
        return default;
    }

    public ValueTask<IReadOnlyList<Burbuja.Core.Abstractions.Outbox.OutboxRecord>> ClaimAsync(int max, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var claimed = new List<Burbuja.Core.Abstractions.Outbox.OutboxRecord>();
        foreach (var kv in _items.ToArray())
        {
            if (claimed.Count >= max)
                break;
            var rec = kv.Value;
            if (rec.Status != Burbuja.Core.Abstractions.Outbox.OutboxStatus.Pending)
                continue;
            if (rec.NextAttemptAt > now)
                continue;
            var inflight = rec with { Status = Burbuja.Core.Abstractions.Outbox.OutboxStatus.InFlight, Attempts = rec.Attempts + 1 };
            if (_items.TryUpdate(kv.Key, inflight, rec))
                claimed.Add(inflight);
        }

        return new(claimed);
    }

    public ValueTask MarkSentAsync(Guid messageId, CancellationToken ct)
    {
        if (_items.TryGetValue(messageId, out var rec))
            _items[messageId] = rec with { Status = Burbuja.Core.Abstractions.Outbox.OutboxStatus.Sent };
        return default;
    }

    public ValueTask MarkDeadAsync(Guid messageId, string reason, CancellationToken ct)
    {
        if (_items.TryGetValue(messageId, out var rec))
            _items[messageId] = rec with { Status = Burbuja.Core.Abstractions.Outbox.OutboxStatus.Dead, DeadReason = reason };
        return default;
    }

    public ValueTask RetryLaterAsync(Guid messageId, DateTimeOffset nextAttemptAt, CancellationToken ct)
    {
        if (_items.TryGetValue(messageId, out var rec))
            _items[messageId] = rec with { Status = Burbuja.Core.Abstractions.Outbox.OutboxStatus.Pending, NextAttemptAt = nextAttemptAt };
        return default;
    }

    public ValueTask<IReadOnlyList<Burbuja.Core.Abstractions.Outbox.OutboxRecord>> ListDeadAsync(CancellationToken ct) =>
        new(_items.Values.Where(r => r.Status == Burbuja.Core.Abstractions.Outbox.OutboxStatus.Dead).ToArray());
}

public sealed class InMemoryLeaseStore(IClock clock) : Burbuja.Core.Abstractions.Leases.ILeaseStore
{
    private readonly ConcurrentDictionary<string, Burbuja.Core.Abstractions.Leases.LeaseAcquisition> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _fences = new(StringComparer.Ordinal);

    public ValueTask<Burbuja.Core.Abstractions.Leases.LeaseAcquisition?> AcquireAsync(string resource, string owner, TimeSpan ttl, CancellationToken ct)
    {
        var now = clock.UtcNow;
        while (true)
        {
            if (_leases.TryGetValue(resource, out var existing) && existing.ExpiresAt > now)
                return new(default(Burbuja.Core.Abstractions.Leases.LeaseAcquisition?));

            var fence = _fences.AddOrUpdate(resource, 1, (_, n) => n + 1);
            var acquired = new Burbuja.Core.Abstractions.Leases.LeaseAcquisition(resource, owner, fence.ToString(), now + ttl);
            if (existing is null)
            {
                if (_leases.TryAdd(resource, acquired))
                    return new(acquired);
            }
            else if (_leases.TryUpdate(resource, acquired, existing))
            {
                return new(acquired);
            }
        }
    }

    public ValueTask<bool> RenewAsync(string resource, string owner, string fencingToken, TimeSpan ttl, CancellationToken ct)
    {
        if (!_leases.TryGetValue(resource, out var existing))
            return new(false);
        if (existing.Owner != owner || existing.FencingToken != fencingToken)
            return new(false);
        var renewed = existing with { ExpiresAt = clock.UtcNow + ttl };
        return new(_leases.TryUpdate(resource, renewed, existing));
    }

    public ValueTask ReleaseAsync(string resource, string owner, string fencingToken, CancellationToken ct)
    {
        if (_leases.TryGetValue(resource, out var existing)
            && existing.Owner == owner && existing.FencingToken == fencingToken)
            _leases.TryRemove(resource, out _);
        return default;
    }

    public ValueTask<Burbuja.Core.Abstractions.Leases.LeaseAcquisition?> GetAsync(string resource, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(_leases.TryGetValue(resource, out var item) ? item : null);
    }

    public ValueTask<IReadOnlyList<Burbuja.Core.Abstractions.Leases.LeaseAcquisition>> ListExpiredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        return new(_leases.Values.Where(v => v.ExpiresAt <= now).ToArray());
    }
}

public sealed class LocalFileArtifactStore(string root, IRedactor redactor) : Burbuja.Core.Abstractions.Artifacts.IArtifactStore
{
    public async ValueTask PutAsync(string key, Stream content, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var textLike = contentType.Length == 0
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
        if (textLike)
        {
            using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct);
            await File.WriteAllTextAsync(path, redactor.Redact(text), ct);
        }
        else
        {
            await using var fs = File.Create(path);
            await content.CopyToAsync(fs, ct);
        }

        await File.WriteAllTextAsync(path + ".meta", contentType, ct);
    }

    public async ValueTask<Burbuja.Core.Abstractions.Artifacts.ArtifactBlob?> GetAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
            return null;
        var type = File.Exists(path + ".meta") ? await File.ReadAllTextAsync(path + ".meta", ct) : "application/octet-stream";
        return new Burbuja.Core.Abstractions.Artifacts.ArtifactBlob(key, File.OpenRead(path), type, null, new Dictionary<string, string>());
    }

    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        return new(File.Exists(path));
    }

    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Invalid artifact key.", nameof(key));
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, key.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(combined, rootFull, StringComparison.Ordinal))
            throw new ArgumentException("Artifact key escapes the store root.", nameof(key));
        return combined;
    }
}

public sealed class InMemoryScheduler(
    TimeProvider time,
    ILogger<InMemoryScheduler> log,
    IOptions<KernelOptions>? kernel = null) : Microsoft.Extensions.Hosting.IHostedService, Burbuja.Core.Abstractions.Scheduling.IScheduler
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed record Job(DateTimeOffset Due, Func<CancellationToken, ValueTask> Work, TimeSpan? Every);

    public void Schedule(string id, DateTimeOffset due, Func<CancellationToken, ValueTask> work, TimeSpan? every = null) =>
        _jobs[id] = new Job(due, work, every);

    public void Cancel(string id) => _jobs.TryRemove(id, out _);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var role = kernel?.Value.Role ?? "all";
        if (!role.Equals("control-plane", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Loop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_loop is not null)
            await _loop.WaitAsync(cancellationToken);
    }

    private async Task Loop(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = time.GetUtcNow();
                foreach (var (id, job) in _jobs.ToArray())
                {
                    if (job.Due > now)
                        continue;
                    if (!_jobs.TryRemove(id, out var due))
                        continue;
                    if (due.Every is { } every)
                        _jobs[id] = due with { Due = now + every };
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await due.Work(ct);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "Scheduler job {JobId} failed", id);
                        }
                    }, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
