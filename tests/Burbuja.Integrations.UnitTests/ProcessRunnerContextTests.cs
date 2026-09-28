using System.Collections.Concurrent;
using Burbuja.TestKit;
using Xunit;

namespace Burbuja.Integrations.UnitTests;

public sealed class ProcessRunnerContextTests
{
    [Fact]
    public async Task A_synchronous_fixture_can_wait_for_a_child_without_pumping_its_context()
    {
        var context = new QueuedContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var child = Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                return ProcessRunner.RunAsync("dotnet", ["--version"],
                    AppContext.BaseDirectory, null, timeout.Token).GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        });
        try
        {
            var result = await child.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
            Assert.Empty(context.Posted);
        }
        finally
        {
            // If the regression returns, release the captured continuation after reporting
            // the failed deadline rather than leaving a permanently blocked worker behind.
            while (context.Posted.TryDequeue(out var work))
                work.Callback(work.State);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Posted { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state) => Posted.Enqueue((callback, state));
    }
}
