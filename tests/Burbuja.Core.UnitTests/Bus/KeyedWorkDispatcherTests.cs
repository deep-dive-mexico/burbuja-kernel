using System.Collections.Concurrent;
using Burbuja.Core.Bus;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Bus;

/// <summary>
/// The dispatcher exists because the in-memory bus delivers a topic on ONE serial pump: a handler
/// that parks on a human (or runs for minutes) used to stop every later message behind it. These
/// facts pin the three properties that make swapping the pump out safe — real concurrency across
/// keys, strict one-at-a-time order within a key, and no lost message anywhere.
/// </summary>
public sealed class KeyedWorkDispatcherTests
{
    private sealed record Work(string Key, int Generation);

    private static Work W(string key, int generation = 0) => new(key, generation);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_parked_item_does_not_hold_the_others()
    {
        // The exact M6.0 wedge: item "blocked" parks until released, and "free" must still run.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freeRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            async (w, _, ct) =>
            {
                if (w.Key == "blocked")
                {
                    entered.TrySetResult();
                    await released.Task.WaitAsync(ct);
                    return;
                }

                freeRan.TrySetResult();
            },
            concurrency: 4);

        dispatcher.TryPost(W("blocked")).ShouldBeTrue();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        dispatcher.TryPost(W("free")).ShouldBeTrue();
        await freeRan.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        released.TrySetResult();
    }

    [Fact]
    public async Task One_key_never_runs_twice_at_once_and_every_message_runs()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var completed = 0;
        var order = new ConcurrentQueue<int>();
        var gate = new SemaphoreSlim(0);
        var entered = new CountdownEvent(4);

        await using var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            async (w, _, ct) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                order.Enqueue(w.Generation);
                entered.Signal();
                await gate.WaitAsync(ct);
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref completed);
            },
            concurrency: 4);

        for (var i = 1; i <= 4; i++)
            dispatcher.TryPost(W("same-key", i)).ShouldBeTrue();

        // Four runs of ONE key must never be inside at once: the countdown reaches zero only if
        // all four entered, and the other three are still queued behind the first.
        entered.Wait(TimeSpan.FromSeconds(1), Ct).ShouldBeFalse();
        Volatile.Read(ref maxConcurrent).ShouldBe(1);

        // Release one at a time. All four messages run, in arrival order, never two at once.
        for (var i = 1; i <= 4; i++)
        {
            gate.Release();
            await WaitUntilAsync(() => Volatile.Read(ref completed) == i, TimeSpan.FromSeconds(5));
            Volatile.Read(ref completed).ShouldBe(i);
        }

        order.ShouldBe([1, 2, 3, 4]);
        Volatile.Read(ref maxConcurrent).ShouldBe(1);
        Volatile.Read(ref concurrent).ShouldBe(0);
    }

    [Fact]
    public async Task Different_keys_run_concurrently()
    {
        var both = new CountdownEvent(2);
        var gate = new SemaphoreSlim(0);

        await using var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            async (_, _, ct) =>
            {
                both.Signal();
                await gate.WaitAsync(ct);
            },
            concurrency: 4);

        dispatcher.TryPost(W("agent-a"));
        dispatcher.TryPost(W("agent-b"));

        // Both lanes must be inside at the same time — that is the whole point of the swap.
        both.Wait(TimeSpan.FromSeconds(5), Ct).ShouldBeTrue();
        gate.Release(2);
    }

    [Fact]
    public async Task A_message_that_arrives_mid_run_is_never_lost()
    {
        var runs = new ConcurrentQueue<int>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            async (w, _, ct) =>
            {
                runs.Enqueue(w.Generation);
                if (w.Generation == 1)
                {
                    firstEntered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return;
                }

                if (w.Generation == 2)
                    secondRun.TrySetResult();
            },
            concurrency: 4);

        dispatcher.TryPost(W("agent-a", 1));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The wake lands while generation 1 is still running. A bare "lease is taken, return"
        // would drop it and the agent would sleep until something else woke it.
        dispatcher.TryPost(W("agent-a", 2));
        dispatcher.TryPost(W("agent-a", 3));

        release.TrySetResult();
        await secondRun.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await WaitUntilAsync(() => runs.Count == 3, TimeSpan.FromSeconds(5));
        runs.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task A_throwing_handler_costs_one_item_and_the_lane_keeps_going()
    {
        var faults = new ConcurrentQueue<string>();
        var afterBoom = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            (w, _, _) =>
            {
                if (w.Generation == 1)
                    throw new InvalidOperationException("planted");
                afterBoom.TrySetResult();
                return ValueTask.CompletedTask;
            },
            concurrency: 2,
            onFault: (_, key) => faults.Enqueue(key));

        dispatcher.TryPost(W("same-key", 1));
        dispatcher.TryPost(W("same-key", 2));

        await afterBoom.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        faults.ShouldContain("same-key");
    }

    [Fact]
    public async Task A_disposed_dispatcher_refuses_work()
    {
        var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            (_, _, _) => ValueTask.CompletedTask,
            concurrency: 1);

        await dispatcher.DisposeAsync(TimeSpan.FromSeconds(5));
        dispatcher.TryPost(W("late")).ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_waits_for_running_work()
    {
        var completed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = new KeyedWorkDispatcher<Work>(
            w => w.Key,
            async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                completed = true;
            },
            concurrency: 1);

        dispatcher.TryPost(W("slow"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var dispose = dispatcher.DisposeAsync(TimeSpan.FromSeconds(5)).AsTask();
        await Task.Delay(100, Ct);
        dispose.IsCompleted.ShouldBeFalse();

        release.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        completed.ShouldBeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25, Ct);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
