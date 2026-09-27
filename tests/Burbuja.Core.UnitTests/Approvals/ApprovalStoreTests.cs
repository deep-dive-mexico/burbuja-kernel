using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Options;
using Burbuja.Core.Redaction;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Approvals;

/// <summary>
/// C7: approvals split into a durable store (records + grants) and process-local waiters, so a
/// process that did not open an approval can still wait for a decision made by another process.
/// </summary>
public sealed class ApprovalStoreTests
{
    [Fact]
    public void Approval_records_and_grants_round_trip_through_the_store()
    {
        IApprovalStore store = new InMemoryApprovalStore();
        var now = DateTimeOffset.UnixEpoch;
        var record = new ApprovalRecord("a1", Request(TimeSpan.FromMinutes(5)), null, now, now.AddMinutes(5), null, null);

        store.Get("a1").ShouldBeNull();
        store.Put(record);
        store.Get("a1").ShouldBe(record);
        store.All().Count.ShouldBe(1);

        store.PutGrant(new ApprovalGrant("dk", null, "stream", "/", now.AddHours(1)));
        store.FindGrant("dk", null, transition: null, now).ShouldNotBeNull();
        store.FindGrant("dk", null, transition: null, now.AddHours(2)).ShouldBeNull();
        store.PurgeGrants(now.AddHours(2));
        store.FindGrant("dk", null, transition: null, now).ShouldBeNull();
    }

    [Fact]
    public async Task Wait_after_a_lost_waiter_polls_the_store_to_a_decision()
    {
        var store = new InMemoryApprovalStore();
        var clock = new TimeProviderClock(TimeProvider.System);
        var events = new InMemoryEventLog(new CaptureGroupRedactor(), clock);
        var policy = Microsoft.Extensions.Options.Options.Create(new PolicyOptions());

        var opener = new InMemoryApprovalService(clock, TimeProvider.System, events, policy, store);
        var other = new InMemoryApprovalService(clock, TimeProvider.System, events, policy, store);

        var id = await opener.OpenAsync(Request(TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
        var waiting = other.WaitAsync(id, TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await opener.ApproveAsync(id, "ok", null, null, TestContext.Current.CancellationToken);

        var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        outcome.ShouldBeOfType<Approved>();
    }

    [Fact]
    public async Task Approving_with_hours_mints_a_grant_that_is_reused()
    {
        var store = new InMemoryApprovalStore();
        var clock = new TimeProviderClock(TimeProvider.System);
        var events = new InMemoryEventLog(new CaptureGroupRedactor(), clock);
        var policy = Microsoft.Extensions.Options.Options.Create(new PolicyOptions());
        var service = new InMemoryApprovalService(clock, TimeProvider.System, events, policy, store);

        var id = await service.OpenAsync(Request(TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
        await service.ApproveAsync(id, "ok", "/subscriptions/s", 4, TestContext.Current.CancellationToken);
        store.FindGrant("dk", null, transition: null, DateTimeOffset.UtcNow).ShouldNotBeNull();

        var second = await service.OpenAsync(Request(TimeSpan.FromMinutes(5)), TestContext.Current.CancellationToken);
        var record = await service.GetAsync(second, TestContext.Current.CancellationToken);
        record!.Outcome.ShouldBeOfType<Approved>().Reused.ShouldBeTrue();
    }

    [Fact]
    public void In_memory_approval_store_satisfies_the_interface()
    {
        IApprovalStore store = new InMemoryApprovalStore();
        var now = DateTimeOffset.UnixEpoch;
        store.PutGrant(new ApprovalGrant("dk", "shape", "s1", null, now.AddHours(1)));
        store.PutGrant(new ApprovalGrant("other", null, "s2", null, now.AddHours(1)));

        store.RemoveGrantsForStream("s1");
        store.FindGrant("dk", "shape", transition: null, now).ShouldBeNull();
        store.FindGrant("other", null, transition: null, now).ShouldNotBeNull();
    }

    private static ApprovalRequest Request(TimeSpan timeout) =>
        new("stream/1", null, ApprovalKind.Permission, "job", "j1", "dk", null, "low", "bash", null, "why", [], true, timeout);
}
