using System.Globalization;
using System.Threading.Channels;
using Burbuja.Core.Arm;

namespace Burbuja.Portal.Services;

/// <summary>One frame as it was delivered to a circuit.</summary>
public sealed record StreamFrame(long Seq, string Type, string Data, DateTimeOffset At);

/// <summary>
/// A server-side SSE reader for one circuit.
///
/// Why server-side: the browser's EventSource cannot send <c>x-api-key</c>, and the portal must
/// never hand it one. So the circuit opens the same stream the kernel offers any ARM client, with
/// the session's key, and renders what arrives.
///
/// The properties the plan asks for are all here:
/// <list type="bullet">
/// <item>a bounded channel (DropOldest) so a slow renderer cannot grow memory without bound;</item>
/// <item>a ~100 ms flush, so 1000 frames are a handful of renders, not 1000;</item>
/// <item><c>Last-Event-ID</c> resume — the reconnect sends the last applied seq, and a frame at or
/// below it is dropped as a replay, so a reconnect produces no duplicate;</item>
/// <item>no gap: the kernel's tail emits every seq after <c>Last-Event-ID</c>, and an
/// <c>event.lag</c> frame (its in-memory tailer overflowed) is counted and surfaced rather than
/// hidden — the range read behind this signal still holds every event;</item>
/// <item>pause on circuit-down: the connection is dropped, and the resume re-reads from the last
/// applied seq.</item>
/// </list>
/// </summary>
public sealed class PortalStream : IAsyncDisposable
{
    private readonly IPortalArm _arm;
    private readonly PortalOptions _options;
    private readonly Channel<ArmSseEvent> _channel;
    private readonly List<StreamFrame> _frames = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    /// <summary>
    /// Serialises DRAINING the channel, which is not the same lock as <see cref="_gate"/>.
    ///
    /// <para>Two drainers is the normal case: the flush timer empties the channel on its own thread every
    /// <see cref="PortalOptions.StreamFlushInterval"/> while whoever is rendering calls <see cref="Flush"/>
    /// too. <see cref="Apply"/> discards a frame at or below the cursor as a replay — the rule that makes a
    /// reconnect safe, and one that is only correct while frames arrive in seq order. Two threads can each
    /// read a frame before either applies it, so without this lock the LATER seq can win the lock inside
    /// <c>Apply</c> first and the earlier frame is then below the cursor and is silently dropped: a real gap
    /// in a feed whose whole contract is that a resume has none, leaving a frame list that still looks
    /// sorted and contiguous. Observed as `[4, 6, 7, …, 46, 48, …, 60]` — five frames gone from the middle
    /// of a sixty-frame feed.</para>
    ///
    /// <para>Lock order is always <c>_drain</c> → <c>_gate</c>, never the reverse.</para>
    /// </summary>
    private readonly object _drain = new();

    private Task? _pump;
    private Task? _flush;

    public PortalStream(string path, IPortalArm arm, PortalOptions options, TimeProvider clock)
    {
        Path = path;
        _arm = arm;
        _options = options;
        Clock = clock;
        // Qualified: Burbuja.Core.Channel (M7.1e) shadows System.Threading.Channels.Channel here.
        _channel = System.Threading.Channels.Channel.CreateBounded<ArmSseEvent>(new BoundedChannelOptions(Math.Max(16, options.StreamBufferSize))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public TimeProvider Clock { get; }

    public string Path { get; }

    public string Status { get; private set; } = "idle";

    /// <summary>The highest seq applied to <see cref="Frames"/>; the resume cursor.</summary>
    public long LastSeq { get; private set; }

    /// <summary>The <c>Last-Event-ID</c> the most recent connection was opened with.</summary>
    public string? LastResumeFrom { get; private set; }

    /// <summary>
    /// The highest seq the wire has DELIVERED, applied or still queued. Written only by the pump (one pump at a
    /// time — <see cref="PumpAsyncIfStopped"/> starts one when the previous completed) and read only there, so
    /// it needs no lock; the resume cursor is <c>max(LastSeq, _receivedSeq)</c>.
    /// </summary>
    private long _receivedSeq;

    public int Applied { get; private set; }

    /// <summary>Frames the kernel's tailer dropped because this reader fell behind (event.lag).</summary>
    public int Lagged { get; private set; }

    /// <summary>Frames this reader dropped because the renderer fell behind.</summary>
    public int Overflowed { get; private set; }

    public int Reconnects { get; private set; }

    public string? LastError { get; private set; }

    public bool Paused { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<StreamFrame> Frames
    {
        get
        {
            lock (_gate)
                return _frames.ToArray();
        }
    }

    public void Start()
    {
        if (_pump is not null)
            return;
        _pump = Task.Run(() => PumpAsync(_cts.Token));
        _flush = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    /// <summary>Circuit down: stop reading and let the connection fail; resume re-reads from LastSeq.</summary>
    public void Pause()
    {
        Paused = true;
        Status = "paused";
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (!Paused)
            return;
        Paused = false;
        Status = "resuming";
        _ = Task.Run(() => PumpAsyncIfStopped());
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a frame. Returns false for a replay — the one rule that makes a reconnect safe:
    /// the resume cursor is the last applied seq, so everything at or below it has been seen.
    /// </summary>
    public bool Apply(ArmSseEvent frame)
    {
        var type = string.IsNullOrEmpty(frame.Name) ? "message" : frame.Name;
        long? seq = long.TryParse(frame.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        lock (_gate)
        {
            if (seq is not null && seq <= LastSeq)
                return false;

            if (type == "event.lag")
                Lagged++;
            if (seq is { } value)
                LastSeq = value;
            _frames.Add(new StreamFrame(seq ?? 0, type, frame.Data, Clock.GetUtcNow()));
            var limit = Math.Max(16, _options.StreamBufferSize);
            while (_frames.Count > limit)
            {
                _frames.RemoveAt(0);
                Overflowed++;
            }

            Applied++;
            return true;
        }
    }

    /// <summary>Records that the wire delivered a frame, for the RESUME cursor. Not the applied cursor: a
    /// queued frame has not reached the list yet, and resuming past it would be a gap if it never did.</summary>
    private void NoteReceived(ArmSseEvent frame)
    {
        if (long.TryParse(frame.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) && seq > _receivedSeq)
            _receivedSeq = seq;
    }

    /// <summary>Drains the channel into the frame list and notifies the renderer once.</summary>
    public int Flush()
    {
        var applied = 0;

        // Read and apply as ONE step, so the frames reach `Apply` in the channel's order. See `_drain`: with
        // two drainers the replay guard would otherwise drop whichever of two concurrently-held frames
        // reached the lock second.
        lock (_drain)
        {
            while (_channel.Reader.TryRead(out var frame))
            {
                if (Apply(frame))
                    applied++;
            }
        }

        lock (_gate)
            Status = Paused ? "paused" : applied > 0 ? "live" : Status;

        if (applied > 0)
            Changed?.Invoke();
        return applied;
    }

    /// <summary>Test/dev helper: flush until the predicate holds or the timeout expires.</summary>
    public async Task<bool> WaitForAsync(Func<PortalStream, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Flush();
            if (predicate(this))
                return true;
            await Task.Delay(5);
        }

        Flush();
        return predicate(this);
    }

    private async Task PumpAsyncIfStopped()
    {
        if (_pump is null || _pump.IsCompleted)
        {
            _cts.Token.ThrowIfCancellationRequested();
            _pump = Task.Run(() => PumpAsync(_cts.Token));
        }

        await Task.CompletedTask;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var backoff = _options.StreamBackoffMin;
        while (!ct.IsCancellationRequested)
        {
            if (Paused)
            {
                await Task.Delay(50, ct);
                continue;
            }

            try
            {
                var client = await _arm.ClientAsync(ct);
                if (client is null)
                {
                    Status = "anonymous";
                    return;
                }

                // M14 R5c (request row K5) — resume from the highest seq this stream has RECEIVED, not from the
                // last one APPLIED.
                //
                // The two are the same on a quiet machine and differ under load: the pump writes a frame to the
                // channel and the flush timer applies it, so a reconnect can happen while frames are still
                // queued — and then `LastSeq` is 0 and the reconnect opened with `Last-Event-ID: null`, asking
                // the kernel to replay a stream it had already read. The property stayed safe (the replay guard
                // drops everything at or below the applied cursor) but it was not deterministic, and the fact
                // that pins the resume (`client.StreamResumeTokens.ShouldContain("2")`) went red about once in
                // three `make test` runs under a loaded machine, which is a lane losing its round-close to a
                // number nobody had promised.
                //
                // Resuming from a seq that is merely QUEUED is safe in both orders: if the queued frame is
                // applied first, the kernel's replay of it is dropped as a replay; if the replay arrives first,
                // the queued copy is. No gap, no duplicate — and the token is now a function of what the wire
                // delivered rather than of when the flush timer happened to run.
                var resumeFrom = Math.Max(LastSeq, _receivedSeq);
                LastResumeFrom = resumeFrom == 0 ? null : resumeFrom.ToString(CultureInfo.InvariantCulture);
                if (Reconnects > 0)
                    Status = "resuming";
                await foreach (var frame in client.StreamAsync(Path, LastResumeFrom, ct))
                {
                    NoteReceived(frame);
                    _channel.Writer.TryWrite(frame);
                    backoff = _options.StreamBackoffMin;
                    if (Paused)
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Status = "reconnecting";
            }

            if (ct.IsCancellationRequested)
                break;
            if (Paused)
            {
                await Task.Delay(50, ct);
                continue;
            }

            Reconnects++;
            Status = "reconnecting";
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 120));
            await Task.Delay(backoff + jitter, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.StreamBackoffMax.Ticks));
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.StreamFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Flush();
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var task in new[] { _pump, _flush })
        {
            if (task is null)
                continue;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _cts.Dispose();
    }
}

/// <summary>
/// The per-circuit stream table. A circuit may hold at most
/// <see cref="PortalOptions.MaxStreamsPerCircuit"/> streams; a page that asks for a fourth gets
/// null and says so, instead of quietly opening an unbounded number of kernel connections.
/// </summary>
public sealed class PortalStreamHub(PortalOptions options, IPortalArm arm, TimeProvider clock) : IAsyncDisposable, IDisposable
{
    private readonly Dictionary<string, PortalStream> _streams = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Limit => options.MaxStreamsPerCircuit;

    public IReadOnlyList<PortalStream> Streams
    {
        get
        {
            lock (_gate)
                return _streams.Values.ToArray();
        }
    }

    /// <summary>The existing stream for this path, a new one, or null when the circuit's cap is reached.</summary>
    public PortalStream? Open(string path)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(path, out var existing))
                return existing;
            if (_streams.Count >= options.MaxStreamsPerCircuit)
                return null;
            var stream = new PortalStream(path, arm, options, clock);
            _streams[path] = stream;
            stream.Start();
            return stream;
        }
    }

    public void Close(string path)
    {
        PortalStream? stream;
        lock (_gate)
        {
            if (!_streams.Remove(path, out stream))
                return;
        }

        _ = stream.DisposeAsync();
    }

    public void PauseAll()
    {
        foreach (var stream in Streams)
            stream.Pause();
    }

    public void ResumeAll()
    {
        foreach (var stream in Streams)
            stream.Resume();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in TakeStreams())
            await stream.DisposeAsync();
    }

    /// <summary>
    /// The SYNC half: a DI container may dispose its scope with <c>Dispose()</c>, and a type implementing only
    /// <c>IAsyncDisposable</c> makes that throw (see `ExecutionView.Dispose` for the measurement — bUnit's
    /// provider is the caller that found it). Each stream's own async disposal is started and not awaited:
    /// releasing the hub is what matters here, and the streams' readers observe their own cancellation.
    /// Idempotent — the second call finds an empty set.
    /// </summary>
    public void Dispose()
    {
        foreach (var stream in TakeStreams())
            _ = stream.DisposeAsync();
    }

    private PortalStream[] TakeStreams()
    {
        lock (_gate)
        {
            var streams = _streams.Values.ToArray();
            _streams.Clear();
            return streams;
        }
    }
}
