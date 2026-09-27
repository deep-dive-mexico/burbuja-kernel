using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.InMemory;
using Burbuja.Core.Options;
using Burbuja.Core.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Burbuja.Core.UnitTests.InMemory;

public sealed class OutboxTests
{
    [Fact]
    public async Task Enqueue_does_not_publish_on_the_caller_stack()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryMessageBus(TimeProvider.System);
        var received = new ConcurrentBag<BusMessage>();
        await using var sub = await bus.SubscribeAsync("topic.a", (m, _) =>
        {
            received.Add(m);
            return default;
        }, ct);
        var box = new InMemoryOutbox();
        await box.EnqueueAsync(Msg("topic.a"), ct);
        await Task.Delay(50, ct);
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_publishes_claimed_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryMessageBus(time);
        var received = new TaskCompletionSource<BusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await bus.SubscribeAsync("topic.a", (m, _) =>
        {
            received.TrySetResult(m);
            return default;
        }, ct);
        var box = new InMemoryOutbox();
        var msg = Msg("topic.a");
        await box.EnqueueAsync(msg, ct);
        var relay = NewRelay(box, bus, time, "control-plane");
        await relay.DrainOnceAsync(ct);
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        got.MessageId.ShouldBe(msg.MessageId);
        var dead = await box.ListDeadAsync(ct);
        dead.ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_dead_letters_after_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new ThrowingBus();
        var box = new InMemoryOutbox();
        await box.EnqueueAsync(Msg("topic.a"), ct);
        var relay = NewRelay(box, bus, time, "control-plane");
        for (var i = 0; i < OutboxRelayService.MaxAttempts; i++)
        {
            await relay.DrainOnceAsync(ct);
            time.Advance(TimeSpan.FromMinutes(2));
        }

        var dead = await box.ListDeadAsync(ct);
        dead.Count.ShouldBe(1);
        dead[0].Status.ShouldBe(OutboxStatus.Dead);
        dead[0].Attempts.ShouldBe(OutboxRelayService.MaxAttempts);
    }

    [Fact]
    public async Task Worker_role_does_not_start_the_relay_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var bus = new InMemoryMessageBus(time);
        var received = new ConcurrentBag<BusMessage>();
        await using var sub = await bus.SubscribeAsync("topic.a", (m, _) =>
        {
            received.Add(m);
            return default;
        }, ct);
        var box = new InMemoryOutbox();
        await box.EnqueueAsync(Msg("topic.a"), ct);
        var relay = NewRelay(box, bus, time, "worker");
        await relay.StartAsync(ct);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, ct);
        received.ShouldBeEmpty();
        await relay.StopAsync(ct);
    }

    private static OutboxRelayService NewRelay(IOutboxStore store, IMessageBus bus, TimeProvider time, string role) =>
        new(
            store,
            bus,
            time,
            MsOptions.Create(new RuntimeOptions { OutboxRelayInterval = TimeSpan.FromMilliseconds(10) }),
            MsOptions.Create(new KernelOptions { Role = role }),
            NullLogger<OutboxRelayService>.Instance);

    private static OutboxMessage Msg(string topic) =>
        new(Guid.NewGuid(), Guid.NewGuid(), topic, new JsonObject { ["k"] = "v" });

    private sealed class ThrowingBus : IMessageBus
    {
        public ValueTask PublishAsync(BusMessage message, CancellationToken ct) =>
            throw new InvalidOperationException("bus down");

        public ValueTask<IAsyncDisposable> SubscribeAsync(string topic, Func<BusMessage, CancellationToken, ValueTask> handler, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

public sealed class BusTests
{
    [Fact]
    public async Task Unsubscribe_stops_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryMessageBus(TimeProvider.System);
        var count = 0;
        var sub = await bus.SubscribeAsync("t", (_, _) =>
        {
            Interlocked.Increment(ref count);
            return default;
        }, ct);
        await bus.PublishAsync(new BusMessage(Guid.NewGuid(), "t", new JsonObject(), new Dictionary<string, string>()), ct);
        await WaitUntil(() => Volatile.Read(ref count) == 1, ct);
        await sub.DisposeAsync();
        await bus.PublishAsync(new BusMessage(Guid.NewGuid(), "t", new JsonObject(), new Dictionary<string, string>()), ct);
        await Task.Delay(50, ct);
        Volatile.Read(ref count).ShouldBe(1);
    }

    [Fact]
    public async Task Slow_handler_does_not_block_publish()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryMessageBus(TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await bus.SubscribeAsync("t", async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }, ct);
        var publish = bus.PublishAsync(new BusMessage(Guid.NewGuid(), "t", new JsonObject(), new Dictionary<string, string>()), ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await publish.AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
        release.TrySetResult();
    }

    [Fact]
    public async Task Seeded_injector_drops_deterministically()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new BusFaultInjector { Seed = 7, DropProbability = 1 };
        var bus = new InMemoryMessageBus(TimeProvider.System, faults);
        var count = 0;
        await using var sub = await bus.SubscribeAsync("t", (_, _) =>
        {
            Interlocked.Increment(ref count);
            return default;
        }, ct);
        await bus.PublishAsync(new BusMessage(Guid.NewGuid(), "t", new JsonObject(), new Dictionary<string, string>()), ct);
        await Task.Delay(50, ct);
        count.ShouldBe(0);
    }

    private static async Task WaitUntil(Func<bool> pred, CancellationToken ct)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!pred())
        {
            if (DateTime.UtcNow > end)
                throw new TimeoutException();
            await Task.Delay(10, ct);
        }
    }
}

public sealed class LeaseTests
{
    [Fact]
    public async Task Fencing_token_is_monotonic_per_resource()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var store = new InMemoryLeaseStore(clock);
        var a = await store.AcquireAsync("r", "o1", TimeSpan.FromSeconds(10), ct);
        a!.FencingToken.ShouldBe("1");
        (await store.AcquireAsync("r", "o2", TimeSpan.FromSeconds(10), ct)).ShouldBeNull();
        await store.ReleaseAsync("r", "o1", "1", ct);
        var b = await store.AcquireAsync("r", "o2", TimeSpan.FromSeconds(10), ct);
        b!.FencingToken.ShouldBe("2");
        long.Parse(b.FencingToken).ShouldBeGreaterThan(long.Parse(a.FencingToken));
    }

    [Fact]
    public async Task Get_and_list_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var store = new InMemoryLeaseStore(clock);
        var a = await store.AcquireAsync("r", "o1", TimeSpan.FromSeconds(5), ct);
        (await store.GetAsync("r", ct))!.FencingToken.ShouldBe(a!.FencingToken);
        (await store.ListExpiredAsync(ct)).ShouldBeEmpty();
        clock.Advance(TimeSpan.FromSeconds(6));
        var expired = await store.ListExpiredAsync(ct);
        expired.Count.ShouldBe(1);
        expired[0].Resource.ShouldBe("r");
        var next = await store.AcquireAsync("r", "o2", TimeSpan.FromSeconds(5), ct);
        next!.FencingToken.ShouldBe("2");
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}

public sealed class SchedulerTests
{
    [Fact]
    public async Task Failing_job_does_not_kill_the_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var scheduler = new InMemoryScheduler(time, NullLogger<InMemoryScheduler>.Instance);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Schedule("bad", time.GetUtcNow(), _ => throw new InvalidOperationException("boom"));
        scheduler.Schedule("ok", time.GetUtcNow(), _ =>
        {
            ran.TrySetResult();
            return default;
        });
        await scheduler.StartAsync(ct);
        await PumpUntil(time, () => ran.Task.IsCompleted, ct);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await scheduler.StopAsync(ct);
    }

    [Fact]
    public async Task Recurrence_fires_more_than_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var scheduler = new InMemoryScheduler(time, NullLogger<InMemoryScheduler>.Instance);
        var count = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Schedule("tick", time.GetUtcNow(), _ =>
        {
            if (Interlocked.Increment(ref count) >= 2)
                second.TrySetResult();
            return default;
        }, TimeSpan.FromSeconds(1));
        await scheduler.StartAsync(ct);
        await PumpUntil(time, () => second.Task.IsCompleted, ct, extra: TimeSpan.FromSeconds(1));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await scheduler.StopAsync(ct);
        count.ShouldBeGreaterThanOrEqualTo(2);
    }

    private static async Task PumpUntil(FakeTimeProvider time, Func<bool> done, CancellationToken ct, TimeSpan? extra = null)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!done())
        {
            if (DateTime.UtcNow > end)
                throw new TimeoutException();
            time.Advance(TimeSpan.FromMilliseconds(50));
            if (extra is { } e)
                time.Advance(e);
            await Task.Delay(10, ct);
        }
    }
}

public sealed class DecisionCacheTests
{
    [Fact]
    public async Task Entries_are_isolated_by_stream_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemoryDecisionCache<string>();
        await cache.SetAsync("stream-a", "k", "alpha", ct);
        await cache.SetAsync("stream-b", "k", "beta", ct);
        (await cache.GetAsync("stream-a", "k", ct)).ShouldBe("alpha");
        (await cache.GetAsync("stream-b", "k", ct)).ShouldBe("beta");
        (await cache.GetAsync("stream-a", "missing", ct)).ShouldBeNull();
    }
}
