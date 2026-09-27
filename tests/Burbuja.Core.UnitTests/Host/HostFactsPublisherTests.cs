using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Channel;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Host;
using Burbuja.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Host;

/// <summary>
/// M7.1d/e. The publisher is the consumer the host-facts provider was built for: it turns a reading
/// into a monitor-lane event and into a human notice. The property that matters most is that a
/// threshold is announced on the CROSSING — a machine that sits at 8 GB free for a week is one
/// message, not ten thousand.
/// </summary>
public sealed class HostFactsPublisherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeProvider(HostFacts? facts, string platform = "macos") : IHostFactsProvider
    {
        public string Platform { get; } = platform;

        public HostFacts? Facts { get; set; } = facts;

        public int Collections { get; private set; }

        public ValueTask<HostFacts?> CollectAsync(CancellationToken ct)
        {
            Collections++;
            return new(Facts);
        }
    }

    private sealed class CapturingEventLog : IEventLog
    {
        public List<EventAppend> Appended { get; } = [];

        public ValueTask<long> AppendAsync(EventAppend evt, CancellationToken ct)
        {
            Appended.Add(evt);
            return new(Appended.Count);
        }

        public IAsyncEnumerable<LedgerEvent> ReadRangeAsync(string streamId, long fromSeq, long toSeq, int limit, RenderLane? lane, CancellationToken ct) =>
            Empty<LedgerEvent>();

        public IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct) =>
            Empty<LedgerEvent>();

        public IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct) =>
            Empty<LedgerEvent>();

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<Notice> Notices { get; } = [];

        public ValueTask NotifyAsync(Notice notice, CancellationToken ct)
        {
            Notices.Add(notice);
            return default;
        }
    }

    private sealed class Holder : IHostFactsSnapshot
    {
        public HostFacts? Latest { get; private set; }

        public void Publish(HostFacts facts) => Latest = facts;
    }

    private static HostFacts Facts(
        decimal? batteryPercent = 80m,
        bool? charging = false,
        decimal? diskFreeGb = 500m,
        decimal? pressure = 55m,
        int? estimateMinutes = null) =>
        new(
            new BatteryFacts(batteryPercent, charging, estimateMinutes),
            MemoryFreeMb: 4000m,
            MemoryPressureLevel: pressure,
            SwapUsedMb: 100m,
            DiskFreeGb: diskFreeGb,
            CpuSpeedLimit: null,
            UptimeSec: 1000m,
            SleepDisabled: true,
            TailscaleOnline: true,
            ProcessRssMb: 50m,
            Extra: new Dictionary<string, string>(),
            At: At);

    private static (HostFactsPublisher Publisher, FakeProvider Provider, CapturingEventLog Events, RecordingNotifier Notifier, Holder Snapshot) Build(HostFacts? facts)
    {
        var provider = new FakeProvider(facts);
        var events = new CapturingEventLog();
        var notifier = new RecordingNotifier();
        var snapshot = new Holder();
        var publisher = new HostFactsPublisher(
            [provider],
            snapshot,
            events,
            notifier,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new RuntimeOptions()),
            Microsoft.Extensions.Options.Options.Create(new KernelOptions { Role = "all" }),
            NullLogger<HostFactsPublisher>.Instance);
        return (publisher, provider, events, notifier, snapshot);
    }

    [Fact]
    public async Task A_reading_is_published_to_the_snapshot_and_the_monitor_lane()
    {
        var (publisher, _, events, notifier, snapshot) = Build(Facts());

        await publisher.CollectOnceAsync(Ct);

        snapshot.Latest.ShouldNotBeNull();
        var evt = events.Appended.ShouldHaveSingleItem();
        evt.StreamId.ShouldBe(HostFactsPublisher.StreamId);
        evt.Type.ShouldBe(HostFactsPublisher.EventType);
        evt.Lanes.ShouldBe(RenderLane.Monitor, "host facts are monitor-lane, never a job output");
        evt.Payload!["battery"]!["percent"]!.GetValue<decimal>().ShouldBe(80m);
        notifier.Notices.ShouldBeEmpty("nothing is wrong with this machine");
    }

    [Theory]
    [InlineData(15, false)]   // the charge the owner asked to hear about: 15 alerts
    [InlineData(9, false)]
    public async Task A_battery_at_or_below_15_off_the_charger_is_an_alert(decimal percent, bool charging)
    {
        var (publisher, _, _, notifier, _) = Build(Facts(batteryPercent: percent, charging: charging));

        await publisher.CollectOnceAsync(Ct);

        var notice = notifier.Notices.ShouldHaveSingleItem();
        notice.Kind.ShouldBe(NoticeKind.Alert);
        notice.Text.ShouldContain("Batería");
    }

    [Fact]
    public async Task One_percent_above_the_floor_is_still_quiet()
    {
        // The boundary is the number itself, so it has to be asserted on both sides: a `<=` that was
        // really a `<` would still pass the fact above and would move the owner's 15% to 14%.
        var (publisher, _, _, notifier, _) = Build(Facts(batteryPercent: 16m));

        await publisher.CollectOnceAsync(Ct);

        notifier.Notices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(42, "🔋 Batería al 14% y sin cargador · quedan ~42 min.")]
    [InlineData(125, "🔋 Batería al 14% y sin cargador · quedan ~2 h 05 min.")]
    [InlineData(120, "🔋 Batería al 14% y sin cargador · quedan ~2 h 00 min.")]
    public async Task The_alert_says_how_long_is_left_when_the_reading_carries_an_estimate(int minutes, string expected)
    {
        var (publisher, _, _, notifier, _) = Build(Facts(batteryPercent: 14m, estimateMinutes: minutes));

        await publisher.CollectOnceAsync(Ct);

        var notice = notifier.Notices.ShouldHaveSingleItem();
        notice.Text.ShouldBe(expected);
    }

    [Fact]
    public async Task The_alert_stops_at_the_percentage_when_there_is_no_estimate()
    {
        // A machine whose gauge and pmset both had nothing still has to be told about: the number the
        // alert exists for is the charge, and the time left is a bonus, not a precondition.
        var (publisher, _, _, notifier, _) = Build(Facts(batteryPercent: 14m, estimateMinutes: null));

        await publisher.CollectOnceAsync(Ct);

        var notice = notifier.Notices.ShouldHaveSingleItem();
        notice.Text.ShouldBe("🔋 Batería al 14% y sin cargador.");
    }

    [Fact]
    public async Task A_low_battery_that_is_charging_is_not_worth_waking_anyone()
    {
        var (publisher, _, _, notifier, _) = Build(Facts(batteryPercent: 12m, charging: true));

        await publisher.CollectOnceAsync(Ct);

        notifier.Notices.ShouldBeEmpty("plugged in at 12% is the charger working, not a problem");
    }

    [Fact]
    public async Task A_low_disk_and_memory_pressure_each_raise_their_own_notice()
    {
        var (publisher, _, _, notifier, _) = Build(Facts(diskFreeGb: 4m, pressure: 11m));

        await publisher.CollectOnceAsync(Ct);

        notifier.Notices.Count.ShouldBe(2);
        notifier.Notices.ShouldContain(n => n.Text.Contains("GB libres"));
        notifier.Notices.ShouldContain(n => n.Text.Contains("Presión de memoria"));
    }

    [Fact]
    public async Task A_threshold_that_stays_crossed_is_announced_once()
    {
        // The whole reason edge-triggering exists: a machine parked at 4 GB free must not send a
        // message every minute for a week.
        var (publisher, _, _, notifier, _) = Build(Facts(diskFreeGb: 4m));

        for (var i = 0; i < 5; i++)
            await publisher.CollectOnceAsync(Ct);

        notifier.Notices.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_threshold_that_clears_and_crosses_again_is_announced_again()
    {
        var (publisher, provider, _, notifier, _) = Build(Facts(diskFreeGb: 4m));
        await publisher.CollectOnceAsync(Ct);
        notifier.Notices.Count.ShouldBe(1);

        provider.Facts = Facts(diskFreeGb: 400m);
        await publisher.CollectOnceAsync(Ct);
        notifier.Notices.Count.ShouldBe(1, "recovery is silent");

        provider.Facts = Facts(diskFreeGb: 3m);
        await publisher.CollectOnceAsync(Ct);
        notifier.Notices.Count.ShouldBe(2, "a second crossing is a second warning");
    }

    [Fact]
    public async Task A_provider_that_returns_nothing_appends_nothing()
    {
        var (publisher, _, events, notifier, snapshot) = Build(facts: null);

        (await publisher.CollectOnceAsync(Ct)).ShouldBeNull();
        events.Appended.ShouldBeEmpty();
        notifier.Notices.ShouldBeEmpty();
        snapshot.Latest.ShouldBeNull();
    }

    [Fact]
    public async Task With_no_provider_registered_the_host_still_starts()
    {
        // A Linux host today, or a host with no HostFacts plugin staged: starting a loop that can
        // never produce anything would be noise, and refusing to start would be worse.
        var publisher = new HostFactsPublisher(
            [],
            new Holder(),
            new CapturingEventLog(),
            new RecordingNotifier(),
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new RuntimeOptions()),
            Microsoft.Extensions.Options.Options.Create(new KernelOptions { Role = "all" }),
            NullLogger<HostFactsPublisher>.Instance);

        await publisher.StartAsync(Ct);
        await publisher.StopAsync(Ct);
    }
}
