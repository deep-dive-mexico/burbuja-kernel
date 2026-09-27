using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Scheduling;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Arm;
using Burbuja.Core.Options;
using Burbuja.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Scheduling;

/// <summary>
/// The two properties that make a durable schedule durable: a window fires exactly once however many
/// times it is ticked, and the answer is recomputed from the store rather than remembered — so a
/// second runner over the same store, which is what a restart or a second node looks like, fires
/// nothing.
/// </summary>
public sealed class DurableScheduleRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>A schedule source over a plain in-memory resource list; records what it fired.</summary>
    private sealed class FakeSource : IScheduleSource
    {
        private readonly Dictionary<string, string> _crons = new(StringComparer.Ordinal);

        /// <summary>
        /// The anchor the real source persists in its own resource. It is what survives a restart, so
        /// a second runner over the same store must see it — the in-memory claim does not.
        /// </summary>
        public Dictionary<string, DateTimeOffset> LastFired { get; } = new(StringComparer.Ordinal);

        public List<(string Key, DateTimeOffset Due)> Fired { get; } = [];

        public int MarkAttempts { get; private set; }

        /// <summary>When false, MarkFiredAsync reports that another writer won the CAS.</summary>
        public bool MarkWins { get; set; } = true;

        /// <summary>Set to make FireAsync throw once, to prove the window is not silently swallowed.</summary>
        public Exception? FireThrows { get; set; }

        public string Namespace => "Test.Ns";

        public string Type => "schedules";

        public void Cron(string key, string cron) => _crons[key] = cron;

        public ScheduleDefinition? Read(ResourceEnvelope<JsonObject> envelope) =>
            _crons.TryGetValue(envelope.Id, out var cron)
                ? new ScheduleDefinition(
                    cron,
                    Utc,
                    Paused: false,
                    LastFiredAt: LastFired.TryGetValue(envelope.Id, out var at) ? at : null)
                : null;

        public ValueTask FireAsync(ResourceEnvelope<JsonObject> envelope, DateTimeOffset scheduledFor, CancellationToken ct)
        {
            if (FireThrows is { } ex)
            {
                FireThrows = null;
                throw ex;
            }

            Fired.Add((envelope.Id, scheduledFor));
            return default;
        }

        public ValueTask<bool> MarkFiredAsync(
            ResourceEnvelope<JsonObject> envelope,
            DateTimeOffset firedAt,
            DateTimeOffset? nextDue,
            CancellationToken ct)
        {
            MarkAttempts++;
            if (MarkWins)
                LastFired[envelope.Id] = firedAt;
            return new(MarkWins);
        }
    }

    private static async Task<(DurableScheduleRunner Runner, FakeSource Source, InMemoryResourceStore Store, FakeTimeProvider Clock, List<EventAppend> Events)>
        BuildAsync(string cron = "*/2 * * * *", string key = "sched-1")
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryResourceStore(new Burbuja.Core.Clock.TimeProviderClock(clock));
        var source = new FakeSource();
        source.Cron(key, cron);
        var events = new List<EventAppend>();
        var log = new CapturingEventLog(events);
        var cache = new InMemoryCache();
        var options = Microsoft.Extensions.Options.Options.Create(new RuntimeOptions());

        var runner = new DurableScheduleRunner(
            [source],
            store,
            cache,
            log,
            clock,
            options,
            Microsoft.Extensions.Options.Options.Create(new KernelOptions()),
            NullLogger<DurableScheduleRunner>.Instance);

        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                key,
                key,
                // The store matches a query's Namespace as a prefix of the envelope's fully-qualified
                // Type and its Type as a "/"-suffix — the same shape PomnibotConstants.ResourceType
                // produces for the real RPs.
                "Test.Ns/schedules",
                "local",
                new Dictionary<string, string>(),
                new JsonObject(),
                ProvisioningState.Succeeded,
                new SystemData(clock.GetUtcNow(), "test", "System", clock.GetUtcNow(), "test", "System"),
                ""),
            null,
            Ct);

        return (runner, source, store, clock, events);
    }

    [Fact]
    public async Task A_window_fires_once_however_many_times_it_is_ticked()
    {
        var (runner, source, _, clock, _) = await BuildAsync("*/2 * * * *");

        // The schedule's anchor is its creation time (10:00), so the first window is 10:02.
        clock.Advance(TimeSpan.FromMinutes(1));            // 10:01 — not due
        await runner.SweepOnceAsync(Ct);
        source.Fired.ShouldBeEmpty("10:01 is not on the */2 grid");

        clock.Advance(TimeSpan.FromMinutes(1));            // 10:02 — due
        await runner.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1);

        // Ticking three more times inside the same window must not fire it again.
        await runner.SweepOnceAsync(Ct);
        await runner.SweepOnceAsync(Ct);
        await runner.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1, "the window is claimed once, not once per tick");
    }

    [Fact]
    public async Task A_second_runner_over_the_same_store_fires_nothing()
    {
        var (runner, source, store, clock, _) = await BuildAsync("*/2 * * * *");
        clock.Advance(TimeSpan.FromMinutes(2));
        await runner.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1);

        // A restart (or a second node) with a cold cache over the same store: the window is still
        // claimed, and the store is the only shared truth.
        var restarted = new DurableScheduleRunner(
            [source],
            store,
            new InMemoryCache(),
            new CapturingEventLog([]),
            clock,
            Microsoft.Extensions.Options.Options.Create(new RuntimeOptions()),
            Microsoft.Extensions.Options.Options.Create(new KernelOptions()),
            NullLogger<DurableScheduleRunner>.Instance);

        await restarted.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1, "the claim is per window, not per process");
    }

    [Fact]
    public async Task A_lost_etag_cas_is_reported_not_treated_as_a_failure()
    {
        var (runner, source, _, clock, events) = await BuildAsync("*/2 * * * *");
        source.MarkWins = false;

        clock.Advance(TimeSpan.FromMinutes(2));
        await runner.SweepOnceAsync(Ct);

        source.Fired.Count.ShouldBe(1, "this node still did the work");
        source.MarkAttempts.ShouldBe(1);
        events.ShouldContain(e => e.Type == ScheduleEvents.SkippedETag);
        events.ShouldNotContain(e => e.Type == ScheduleEvents.Fired);
    }

    [Fact]
    public async Task A_successful_window_emits_schedule_fired_with_the_next_due_time()
    {
        var (runner, _, _, clock, events) = await BuildAsync("*/2 * * * *");
        clock.Advance(TimeSpan.FromMinutes(2));
        await runner.SweepOnceAsync(Ct);

        var fired = events.Single(e => e.Type == ScheduleEvents.Fired);
        fired.StreamId.ShouldBe(ScheduleEvents.StreamId("sched-1"));
        fired.Payload!["scheduledFor"]!.GetValue<string>().ShouldBe(
            new DateTimeOffset(2026, 9, 12, 10, 2, 0, TimeSpan.Zero).ToString("O"));
        fired.Payload["nextRunAt"]!.GetValue<string>().ShouldBe(
            new DateTimeOffset(2026, 9, 12, 10, 4, 0, TimeSpan.Zero).ToString("O"));
    }

    [Fact]
    public async Task A_failed_fire_releases_the_window_so_the_next_tick_retries_it()
    {
        var (runner, source, _, clock, _) = await BuildAsync("*/2 * * * *");
        source.FireThrows = new InvalidOperationException("planted");

        clock.Advance(TimeSpan.FromMinutes(2));
        await runner.SweepOnceAsync(Ct);
        source.Fired.ShouldBeEmpty();

        // The claim must not survive a failed fire, or the window would be silently skipped.
        await runner.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_missed_window_fires_once_when_the_host_comes_back()
    {
        var (runner, source, _, clock, _) = await BuildAsync("*/2 * * * *");

        // The Host was down for an hour: many windows were missed, exactly one fire is owed.
        clock.Advance(TimeSpan.FromHours(1));
        await runner.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1);

        await runner.SweepOnceAsync(Ct);
        source.Fired.Count.ShouldBe(1, "the backlog is not replayed window by window");
    }

    [Fact]
    public async Task An_unparseable_cron_is_skipped_and_never_throws()
    {
        var (runner, source, _, clock, _) = await BuildAsync("not a cron");
        clock.Advance(TimeSpan.FromMinutes(2));
        await runner.SweepOnceAsync(Ct);
        source.Fired.ShouldBeEmpty();
    }

    /// <summary>Minimal fakes: the point under test is the runner's control flow, not the stores.</summary>
    private sealed class CapturingEventLog(List<EventAppend> sink) : IEventLog
    {
        public ValueTask<long> AppendAsync(EventAppend append, CancellationToken ct)
        {
            sink.Add(append);
            return new(sink.Count);
        }

        public IAsyncEnumerable<LedgerEvent> ReadRangeAsync(string streamId, long fromSeq, long toSeq, int limit, RenderLane? lane, CancellationToken ct) =>
            AsyncEnumerable.Empty<LedgerEvent>();

        public IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct) =>
            AsyncEnumerable.Empty<LedgerEvent>();

        public IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct) =>
            AsyncEnumerable.Empty<LedgerEvent>();
    }

    private sealed class InMemoryCache : ICache
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct) =>
            new(_values.TryGetValue(key, out var v) ? (T?)v : default);

        public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct)
        {
            if (value is null)
                _values.Remove(key);
            else
                _values[key] = value;
            return default;
        }

        public ValueTask<bool> TryAddAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct)
        {
            if (_values.ContainsKey(key))
                return new(false);
            _values[key] = value;
            return new(true);
        }
    }
}
