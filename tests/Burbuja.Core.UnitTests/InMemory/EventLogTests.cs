using System.Threading.Channels;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Redaction;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.InMemory;

public sealed class EventLogTests
{
    [Fact]
    public async Task Two_tailers_both_receive_the_same_events()
    {
        var log = NewLog();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var a = Collect(log.TailAsync("s1", 0, cts.Token), 2);
        var b = Collect(log.TailAsync("s1", 0, cts.Token), 2);
        await log.AppendAsync(Evt("s1", "one"), TestContext.Current.CancellationToken);
        await log.AppendAsync(Evt("s1", "two"), TestContext.Current.CancellationToken);
        (await a).Select(e => e.Type).ShouldBe(["one", "two"]);
        (await b).Select(e => e.Type).ShouldBe(["one", "two"]);
    }

    [Fact]
    public async Task Summary_is_truncated_to_512_and_redacted()
    {
        var log = NewLog();
        var secret = "AKIA" + new string('A', 16);
        var seq = await log.AppendAsync(Evt("s1", "t", secret + new string('x', 600)), TestContext.Current.CancellationToken);
        seq.ShouldBe(1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        LedgerEvent? got = null;
        await foreach (var e in log.TailAsync("s1", 0, cts.Token))
        {
            got = e;
            break;
        }

        got.ShouldNotBeNull();
        got.Summary.Length.ShouldBeLessThanOrEqualTo(512);
        got.Summary.ShouldNotContain("AKIA");
        got.Summary.ShouldContain("[redacted:aws-access-key]");
    }

    [Fact]
    public async Task Tail_after_seq_does_not_skip_appends_during_subscribe()
    {
        var log = NewLog();
        var ct = TestContext.Current.CancellationToken;
        await log.AppendAsync(Evt("s1", "a"), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var types = new List<string>();
        var tail = Task.Run(async () =>
        {
            await foreach (var e in log.TailAsync("s1", 0, cts.Token))
            {
                types.Add(e.Type);
                if (types.Count >= 3)
                    break;
            }
        }, ct);
        await log.AppendAsync(Evt("s1", "b"), ct);
        await log.AppendAsync(Evt("s1", "c"), ct);
        await tail.WaitAsync(TimeSpan.FromSeconds(2), ct);
        types.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public async Task TailAll_filters_by_lane()
    {
        var log = NewLog();
        var ct = TestContext.Current.CancellationToken;
        await log.AppendAsync(Evt("s1", "out", lanes: RenderLane.Output), ct);
        await log.AppendAsync(Evt("s1", "log", lanes: RenderLane.Log), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        var types = new List<string>();
        try
        {
            await foreach (var e in log.TailAllAsync(RenderLane.Log, 0, cts.Token))
                types.Add(e.Type);
        }
        catch (OperationCanceledException)
        {
        }

        types.ShouldBe(["log"]);
    }

    [Fact]
    public void Bounded_try_write_rejects_when_full()
    {
        // Qualified: Burbuja.Core.Channel (M7.1e) shadows System.Threading.Channels.Channel here.
        var ch = System.Threading.Channels.Channel.CreateBounded<int>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        var accepted = 0;
        var rejected = 0;
        for (var i = 0; i < 40; i++)
        {
            if (ch.Writer.TryWrite(i))
                accepted++;
            else
                rejected++;
        }

        accepted.ShouldBe(8);
        rejected.ShouldBe(32);
        ch.Reader.Count.ShouldBe(8);
    }

    [Fact]
    public async Task Overflow_emits_event_lag()
    {
        var log = NewLog(tailerCapacity: 8);
        var ct = TestContext.Current.CancellationToken;
        log.AddHungTailer(8);
        log.DebugTailerCount.ShouldBe(1);
        for (var i = 0; i < 40; i++)
            await log.AppendAsync(Evt("s1", $"hung{i}"), ct);
        log.DebugBuffered.ShouldBe(8);
        log.DebugDropped.ShouldBeGreaterThan(0);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await using var enumerator = log.TailAsync("s1", 0, cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync();
        await Task.Delay(30, ct);
        log.DebugTailerCount.ShouldBeGreaterThanOrEqualTo(2);
        for (var i = 0; i < 40; i++)
            await log.AppendAsync(Evt("s1", $"live{i}"), ct);
        (await pending).ShouldBeTrue();
        var types = new List<string> { enumerator.Current.Type };
        while (await enumerator.MoveNextAsync())
        {
            types.Add(enumerator.Current.Type);
            if (enumerator.Current.Type == "event.lag")
                break;
            if (types.Count > 80)
                break;
        }

        types.ShouldContain("event.lag");
    }

    [Fact]
    public async Task Range_read_never_returns_synthetic_event_lag()
    {
        var log = NewLog(tailerCapacity: 8);
        var ct = TestContext.Current.CancellationToken;
        log.AddHungTailer(8);
        for (var i = 0; i < 40; i++)
            await log.AppendAsync(Evt("s1", $"hung{i}"), ct);
        log.DebugDropped.ShouldBeGreaterThan(0);

        var read = new List<LedgerEvent>();
        await foreach (var e in log.ReadRangeAsync("s1", 0, long.MaxValue, 0, null, ct))
            read.Add(e);

        read.Count.ShouldBe(40);
        read.ShouldNotContain(e => e.Type == "event.lag");
        read.Select(e => e.Seq).ShouldBe(Enumerable.Range(1, 40).Select(i => (long)i));
    }

    private static InMemoryEventLog NewLog(int tailerCapacity = 256) =>
        new(new CaptureGroupRedactor(), new TimeProviderClock(TimeProvider.System), tailerCapacity);

    private static EventAppend Evt(string stream, string type, string? summary = null, RenderLane lanes = RenderLane.Log) =>
        new(stream, null, type, summary ?? type, null, lanes, null, null, null);

    private static async Task<List<LedgerEvent>> Collect(IAsyncEnumerable<LedgerEvent> src, int n)
    {
        var list = new List<LedgerEvent>();
        await foreach (var e in src)
        {
            list.Add(e);
            if (list.Count >= n)
                break;
        }

        return list;
    }
}
