using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Leases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using Burbuja.Core.Abstractions.Json;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class JobLeaseTakeoverTests
{
    [Fact]
    public async Task Accepted_cancellation_cannot_be_overwritten_by_a_runner_paused_before_startup()
    {
        PausingLeaseStore? pause = null;
        using var host = Harness.CreateHost(extra: services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(ILeaseStore));
            services.Remove(original);
            services.AddSingleton<ILeaseStore>(provider =>
            {
                var real = original.ImplementationFactory is not null
                    ? (ILeaseStore)original.ImplementationFactory(provider)
                    : (ILeaseStore)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                return pause = new PausingLeaseStore(real, pauseOrchestration: false);
            });
        });
        await Harness.SeedAsync(host);
        await Harness.Send(host, "PUT", Harness.JobPath, Harness.JobBody("happy-path"));
        var running = host.GetRequiredService<JobOrchestrator>().StartAsync(
            Harness.JobPath.ToLowerInvariant(), TestContext.Current.CancellationToken);
        await pause!.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var cancel = await Harness.Send(host, "POST", Harness.JobPath + "/cancel", "{}");
        Assert.Equal(202, cancel.StatusCode);
        pause.Resume.TrySetResult();
        await running;
        var final = await Harness.Send(host, "GET", Harness.JobPath);
        Assert.Equal("cancelled", JsonSerializer.SerializeToNode(final.Result, JsonWire.Options)!["properties"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_expired_claimant_cannot_replace_a_successors_persisted_execution_fence()
    {
        var clock = new FakeTimeProvider();
        PausingLeaseStore? pause = null;
        using var host = Harness.CreateHost(extra: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            var inner = services.Single(descriptor => descriptor.ServiceType == typeof(ILeaseStore));
            services.Remove(inner);
            services.AddSingleton<ILeaseStore>(provider =>
            {
                var real = inner.ImplementationFactory is not null
                    ? (ILeaseStore)inner.ImplementationFactory(provider)
                    : (ILeaseStore)ActivatorUtilities.CreateInstance(provider, inner.ImplementationType!);
                return pause = new PausingLeaseStore(real);
            });
        }, settings: new() { ["Runtime:LeaseTtl"] = "00:00:01" });
        await Harness.SeedAsync(host);
        await Harness.Send(host, "PUT", Harness.JobPath, Harness.JobBody("happy-path"));
        var orchestrator = host.GetRequiredService<JobOrchestrator>();
        var first = orchestrator.StartAsync(Harness.JobPath.ToLowerInvariant(), TestContext.Current.CancellationToken);
        await pause!.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        await orchestrator.StartAsync(Harness.JobPath.ToLowerInvariant(), TestContext.Current.CancellationToken);
        var before = await Harness.Send(host, "GET", Harness.JobPath);
        var fence = JsonSerializer.SerializeToNode(before.Result, JsonWire.Options)!["properties"]!["recovery"]!["fenceToken"]!.GetValue<string>();
        Assert.Equal("2", fence);
        pause.Resume.TrySetResult();
        await first;
        var after = await Harness.Send(host, "GET", Harness.JobPath);
        Assert.Equal(fence, JsonSerializer.SerializeToNode(after.Result, JsonWire.Options)!["properties"]!["recovery"]!["fenceToken"]!.GetValue<string>());
    }

    private sealed class PausingLeaseStore(ILeaseStore inner, bool pauseOrchestration = true) : ILeaseStore
    {
        private int _claims;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<LeaseAcquisition?> AcquireAsync(string resource, string owner, TimeSpan ttl, CancellationToken ct)
        {
            var lease = await inner.AcquireAsync(resource, owner, ttl, ct);
            if (lease is not null && resource.StartsWith("job-orchestration/", StringComparison.Ordinal) == pauseOrchestration
                && Interlocked.Increment(ref _claims) == 1)
            {
                Paused.TrySetResult();
                await Resume.Task.WaitAsync(ct);
            }
            return lease;
        }
        public ValueTask<bool> RenewAsync(string resource, string owner, string fence, TimeSpan ttl, CancellationToken ct) =>
            inner.RenewAsync(resource, owner, fence, ttl, ct);
        public ValueTask ReleaseAsync(string resource, string owner, string fence, CancellationToken ct) =>
            inner.ReleaseAsync(resource, owner, fence, ct);
        public ValueTask<LeaseAcquisition?> GetAsync(string resource, CancellationToken ct) => inner.GetAsync(resource, ct);
        public ValueTask<IReadOnlyList<LeaseAcquisition>> ListExpiredAsync(CancellationToken ct) => inner.ListExpiredAsync(ct);
    }
}
