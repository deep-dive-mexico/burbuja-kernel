using Burbuja.Core.Abstractions.Events;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Burbuja.Portal.Services;

/// <summary>
/// Pauses every stream of a circuit while its connection is down. A disconnected circuit keeps its
/// server-side state for the reconnection window, and holding kernel SSE connections open for a
/// browser that is not listening wastes the kernel's tailers; on reconnect the streams resume from
/// the last applied seq, so nothing is lost.
/// </summary>
public sealed class PortalCircuitHandler(PortalStreamHub hub) : CircuitHandler
{
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        hub.PauseAll();
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        hub.ResumeAll();
        return Task.CompletedTask;
    }
}

/// <summary>
/// One execution's live view: the event range (the trajectory's data source) plus the SSE signal
/// that says more arrived. The stream is only a signal; the data always comes from the range read,
/// which is why a dropped frame can never corrupt the page.
/// </summary>
public sealed class ExecutionView(PortalData data, PortalStreamHub hub, PortalOptions options, TimeProvider clock)
    : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan MinCatchUp = TimeSpan.FromMilliseconds(400);
    private readonly List<LedgerEvent> _events = [];
    private DateTimeOffset _lastCatchUp = DateTimeOffset.MinValue;
    private bool _subscribed;

    public IReadOnlyList<LedgerEvent> Events => _events;

    public PortalStream? Stream { get; private set; }

    public string? StreamPath { get; private set; }

    public string? StreamRefused { get; private set; }

    public PortalFailure? Failure { get; private set; }

    public bool Loading { get; private set; }

    public long LastLoadedSeq { get; private set; }

    public bool Truncated { get; private set; }

    /// <summary>
    /// T4: the words the trajectory is currently filtered by, and what the kernel said about the search
    /// that produced the rows. <see cref="SearchScanned"/> is only meaningful while
    /// <see cref="Query"/> is set; <see cref="SearchComplete"/> distinguishes "no matches" from "no
    /// matches in the part of the stream that was read".
    /// </summary>
    public string? Query { get; private set; }

    public long SearchScanned { get; private set; }

    public bool SearchComplete { get; private set; }

    public bool SearchCapped { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Reads the whole range once and opens the live signal for the same path.
    ///
    /// <para><b>M11 F2 — the view is per-CIRCUIT, and a new path must start from nothing.</b> The view is a
    /// scoped service, so one circuit holds ONE of them across every navigation inside that circuit. The first
    /// version kept <c>_events</c>, the previous <c>Stream</c> and <c>LastLoadedSeq</c> across a path change, so
    /// navigating from a job's trajectory to a turn's trace kept the JOB's stream: <c>Attach</c> returned the
    /// existing stream for the old path, and the "live" trace showed the wrong page's events while the page
    /// looked perfectly healthy. Pre-existing (the job pages had it too), and A7 added a fourth consumer of the
    /// same view and called it "the G10 pattern".</para>
    ///
    /// <para>The three resets are one rule: a stream that belongs to another path, a cursor that counts another
    /// path's sequence, and a list of another path's events are each individually wrong, and the page cannot
    /// tell which one it got.</para>
    /// </summary>
    public async ValueTask LoadAsync(string path, int max, CancellationToken ct)
    {
        if (StreamPath is not null && !string.Equals(StreamPath, path, StringComparison.Ordinal))
        {
            ReleaseStream();
            _events.Clear();
            LastLoadedSeq = 0;
            Truncated = false;
            Query = null;
            StreamRefused = null;
        }

        Failure = null;
        Loading = true;
        Changed?.Invoke();
        try
        {
            await AppendAsync(path, 0, max, ct);
            StreamPath = path;
        }
        catch (Burbuja.Core.Arm.ArmException ex)
        {
            Failure = PortalFailure.From(ex);
        }
        finally
        {
            Loading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// T4: run a search over the whole stream and show the matches in place of the loaded range. An
    /// empty query clears it and puts the range back, so the box is the only state and there is no mode
    /// to get stuck in. The live signal keeps working while a search is showing: catch-up re-runs the
    /// search over the delta, so a turn that lands while the box has words in it appears if it matches.
    /// </summary>
    public async ValueTask SearchAsync(string path, string? query, int max, int maxPages, CancellationToken ct)
    {
        StreamPath = path;
        Query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        Failure = null;
        Loading = true;
        Changed?.Invoke();
        try
        {
            _events.Clear();
            LastLoadedSeq = 0;
            Truncated = false;
            if (Query is null)
            {
                SearchScanned = 0;
                SearchComplete = true;
                SearchCapped = false;
                await AppendAsync(path, 0, max, ct);
                return;
            }

            var page = await data.SearchEventsAsync(path, Query, max, maxPages, ct);
            _events.AddRange(page.Events);
            SearchScanned = page.Scanned;
            SearchComplete = page.Complete;
            SearchCapped = page.Events.Count >= max;
            LastLoadedSeq = _events.Count > 0 ? _events[^1].Seq : long.MaxValue;
        }
        catch (Burbuja.Core.Arm.ArmException ex)
        {
            Failure = PortalFailure.From(ex);
        }
        finally
        {
            Loading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Opens the circuit's SSE reader for this path (interactive render only).</summary>
    public PortalStream? Attach()
    {
        if (StreamPath is null || Stream is not null)
            return Stream;
        Stream = hub.Open(StreamPath);
        if (Stream is null)
        {
            StreamRefused = $"This circuit already holds {hub.Limit} live streams.";
            return null;
        }

        if (!_subscribed)
        {
            Stream.Changed += OnSignal;
            _subscribed = true;
        }

        return Stream;
    }

    /// <summary>Reads the delta between the last loaded event and the last signalled seq.</summary>
    public async ValueTask CatchUpAsync(CancellationToken ct)
    {
        if (StreamPath is null || Stream is null)
            return;
        var target = Stream.LastSeq;
        if (target <= LastLoadedSeq)
            return;
        try
        {
            // A search is not a contiguous suffix of the stream, so it cannot be extended by appending:
            // the delta is searched again and the answer replaces the list. The re-read is bounded by
            // the same page budget and the caller's signal is already coalesced, so a live search costs
            // one search per flush, not one per event.
            if (Query is not null)
            {
                var page = await data.SearchEventsAsync(StreamPath, Query, options.EventPageSize * 8, SearchMaxPages, ct);
                _events.Clear();
                _events.AddRange(page.Events);
                SearchScanned = page.Scanned;
                SearchComplete = page.Complete;
                SearchCapped = page.Events.Count >= options.EventPageSize * 8;
                LastLoadedSeq = target;
                return;
            }

            await AppendAsync(StreamPath, LastLoadedSeq + 1, options.EventPageSize * 4, ct);
        }
        catch (Burbuja.Core.Arm.ArmException ex)
        {
            Failure = PortalFailure.From(ex);
        }
    }

    /// <summary>How many pages one search reads before it reports that it stopped early.</summary>
    public const int SearchMaxPages = 20;

    private void OnSignal()
    {
        if (Loading)
            return;
        var now = clock.GetUtcNow();
        if (now - _lastCatchUp < MinCatchUp)
            return;

        // The signal is cheap and lossy on purpose: the next flush catches whatever this skip missed,
        // and the read itself is cumulative from LastLoadedSeq.
        _lastCatchUp = now;
        _ = Task.Run(async () =>
        {
            await CatchUpAsync(CancellationToken.None);
            Changed?.Invoke();
        });
    }

    private async ValueTask AppendAsync(string path, long from, int max, CancellationToken ct)
    {
        var events = await data.EventsAsync(path, from, max, ct);
        if (events.Count >= max)
            Truncated = true;
        foreach (var evt in events)
        {
            if (evt.Seq <= LastLoadedSeq && _events.Count > 0)
                continue;
            _events.Add(evt);
            if (evt.Seq > LastLoadedSeq)
                LastLoadedSeq = evt.Seq;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseStream();
        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// The SYNC half, and it exists because a DI container can dispose its scope synchronously.
    ///
    /// <para><b>Measured, not defensive.</b> bUnit's service provider disposes its scope with
    /// <c>Dispose()</c>, and a registration whose implementation implements only <c>IAsyncDisposable</c> makes
    /// that throw <c>"…type only implements IAsyncDisposable. Use DisposeAsync to dispose the container"</i> —
    /// which is how the AKOS turn page's three facts failed the moment the page began resolving this view
    /// (A7). The options were to make every test scope async-disposable or to give this type the sync interface
    /// it can trivially honour; the second is one method and no test ceremony.</para>
    ///
    /// <para>Idempotent: releasing twice is releasing once, so an async disposal after a sync one is safe.</para>
    /// </summary>
    public void Dispose() => ReleaseStream();

    private void ReleaseStream()
    {
        if (Stream is null)
            return;
        if (_subscribed)
            Stream.Changed -= OnSignal;
        hub.Close(Stream.Path);
        Stream = null;
    }
}
