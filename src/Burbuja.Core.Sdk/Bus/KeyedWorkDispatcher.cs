using System.Collections.Concurrent;

namespace Burbuja.Core.Bus;

/// <summary>
/// A concurrent replacement for the bus's serial subscriber pump.
///
/// <para>
/// The in-memory bus hands a topic to ONE subscriber on ONE serial pump
/// (<c>InMemoryMessageBus.SubscribeAsync</c>), so a handler that awaits long work — a parked
/// approval waiting on a human, a job running for minutes — stops every later message on that
/// topic behind it. That is a head-of-line block, not a queue: the first parked approval wedges the
/// topic for its whole <c>AgentApprovalWait</c>, and in the M6.0 lane it looked like jobs that
/// "never left queued".
/// </para>
/// <para>
/// A subscriber posts here instead. Each message lands in a per-key FIFO lane. Every message runs
/// exactly once, in arrival order within its key, and never two at a time for the same key (a
/// fenced lease would reject the second anyway, so running it would just lose it). Different keys
/// run concurrently, bounded by the dispatcher's slot count, so one parked item costs one slot
/// instead of the whole topic.
/// </para>
/// <para>
/// Lanes are never evicted. The key space is the resource id space (agents, jobs), which is
/// bounded by the domain, and evicting a lane race-free would mean holding its lock across the
/// dictionary removal — a lost wake is a worse failure than a dictionary entry.
/// </para>
/// </summary>
public sealed class KeyedWorkDispatcher<T> : IAsyncDisposable
    where T : notnull
{
    private sealed class Lane
    {
        public readonly object Gate = new();
        public readonly Queue<T> Items = new();
        public bool Busy;
    }

    private readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private readonly Func<T, string> _keyOf;
    private readonly Func<T, string, CancellationToken, ValueTask> _run;
    private readonly Action<Exception, string>? _onFault;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closed;
    private int _disposed;

    /// <param name="keyOf">The lane key: messages for one key run one at a time, in order.</param>
    /// <param name="run">The handler. Receives the item, its key, and the dispatcher's token.</param>
    /// <param name="concurrency">How many items may run at once across all lanes. At least 1.</param>
    /// <param name="onFault">Called when a handler throws; a fault never stops the dispatcher.</param>
    public KeyedWorkDispatcher(
        Func<T, string> keyOf,
        Func<T, string, CancellationToken, ValueTask> run,
        int concurrency = 4,
        Action<Exception, string>? onFault = null)
    {
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        _keyOf = keyOf;
        _run = run;
        _onFault = onFault;
        _slots = new SemaphoreSlim(concurrency, concurrency);
        Concurrency = concurrency;
    }

    public int Concurrency { get; }

    /// <summary>Keys with work queued or running. Test/diagnostic surface.</summary>
    public int ActiveLanes => _lanes.Count(l => l.Value.Busy);

    /// <summary>
    /// Hand a message to the dispatcher and return immediately, freeing the bus pump. Returns
    /// <c>false</c> only when the dispatcher is already disposed. The message is never dropped and
    /// is never coalesced away: it runs once, after whatever is already queued for its key.
    /// </summary>
    public bool TryPost(T item)
    {
        if (_closed)
            return false;

        var key = _keyOf(item);
        var lane = _lanes.GetOrAdd(key, static _ => new Lane());
        lock (lane.Gate)
        {
            lane.Items.Enqueue(item);
        }

        Arm(key, lane);
        return true;
    }

    /// <summary>Start the lane's drain loop if it has work and nothing is draining it.</summary>
    private void Arm(string key, Lane lane)
    {
        lock (lane.Gate)
        {
            if (lane.Busy || lane.Items.Count == 0 || _closed)
                return;
            lane.Busy = true;
        }

        _ = Task.Run(() => DrainAsync(key, lane), CancellationToken.None);
    }

    private async Task DrainAsync(string key, Lane lane)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                T item;
                lock (lane.Gate)
                {
                    if (lane.Items.Count == 0)
                        break;
                    item = lane.Items.Dequeue();
                }

                await _slots.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await _run(item, key, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // shutting down
                }
                catch (Exception ex)
                {
                    // A throwing handler used to kill the bus pump outright. Here it costs one
                    // item, and the lane keeps its order and its remaining work.
                    _onFault?.Invoke(ex, key);
                }
                finally
                {
                    _slots.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed mid-wait
        }
        finally
        {
            lock (lane.Gate)
            {
                lane.Busy = false;
            }

            // A post that landed between the last dequeue and here would otherwise strand: the
            // lane looked busy, so it was not armed, and nothing else will arm it.
            Arm(key, lane);
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => DisposeAsync(null);

    /// <summary>
    /// Stop accepting work, let what is running finish (bounded by <paramref name="drainTimeout"/>),
    /// then cancel. Idempotent: a Host may call <c>StopAsync</c> and dispose the container, and a
    /// second call must not throw out of shutdown.
    ///
    /// <para>
    /// The <see cref="CancellationTokenSource"/> and the slot semaphore are deliberately never
    /// disposed. Both are still touched by a drain loop that is finishing up — <c>_cts.Token</c> on
    /// each slot wait, <c>_slots.Release()</c> after a run — and using either after disposal throws
    /// <see cref="ObjectDisposedException"/> from inside a background task, which surfaces as
    /// "one or more hosted services failed to stop". Neither owns a timer or an unmanaged handle
    /// once nothing is registered on it, so not disposing them costs nothing and removes the race.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync(TimeSpan? drainTimeout = null)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _closed = true;

        var timeout = drainTimeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline
               && _lanes.Any(l => l.Value.Busy))
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        _cts.Cancel();
    }
}
