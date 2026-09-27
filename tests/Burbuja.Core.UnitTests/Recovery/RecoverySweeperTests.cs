using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Recovery;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Options;
using Burbuja.Core.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Recovery;

/// <summary>
/// C7: the sweeper asks the store for candidates (one page at a time) and calls the handler once
/// per candidate id. Handlers without a query are not swept; terminal states are filtered out.
/// </summary>
public sealed class RecoverySweeperTests
{
    [Fact]
    public async Task Handler_with_a_query_gets_one_call_per_candidate_id()
    {
        var store = NewStore();
        await Seed(store, "w1", ProvisioningState.Creating);
        await Seed(store, "w2", ProvisioningState.Accepted);
        var handler = new SpyHandler(new RecoveryQuery("Burbuja.Compute", "workspaces", ["Creating", "Accepted"]));

        await Sweep(store, handler);

        handler.Recovered.OrderBy(x => x).ShouldBe(["/w1", "/w2"]);
        handler.Recovered.Count.ShouldBe(handler.Recovered.Distinct().Count());
    }

    [Fact]
    public async Task Handler_without_a_query_is_not_swept()
    {
        var store = NewStore();
        await Seed(store, "w1", ProvisioningState.Creating);
        var handler = new SpyHandler(query: null);

        await Sweep(store, handler);

        handler.Recovered.ShouldBeEmpty();
    }

    [Fact]
    public async Task Terminal_resources_are_not_swept()
    {
        var store = NewStore();
        await Seed(store, "live", ProvisioningState.Creating);
        await Seed(store, "done", ProvisioningState.Succeeded);
        await Seed(store, "dead", ProvisioningState.Failed);
        var handler = new SpyHandler(new RecoveryQuery("Burbuja.Compute", "workspaces", ["Creating"]));

        await Sweep(store, handler);

        handler.Recovered.ShouldBe(["/live"]);
    }

    [Fact]
    public async Task Sweep_pages_beyond_one_page()
    {
        var store = NewStore();
        for (var i = 0; i < 5; i++)
            await Seed(store, $"w{i}", ProvisioningState.Creating);
        var handler = new SpyHandler(new RecoveryQuery("Burbuja.Compute", "workspaces", ["Creating"], PageSize: 2));

        await Sweep(store, handler);

        handler.Recovered.Count.ShouldBe(5);
        handler.Recovered.Distinct().Count().ShouldBe(5);
    }

    private static async Task Sweep(InMemoryResourceStore store, SpyHandler handler)
    {
        var sweeper = new RecoverySweeper(
            [handler],
            store,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new RuntimeOptions()),
            Microsoft.Extensions.Options.Options.Create(new KernelOptions { Role = "all" }),
            NullLogger<RecoverySweeper>.Instance);
        await sweeper.SweepOnceAsync(TestContext.Current.CancellationToken);
    }

    private static InMemoryResourceStore NewStore() =>
        new(new TimeProviderClock(TimeProvider.System));

    private static async Task Seed(InMemoryResourceStore store, string name, ProvisioningState state)
    {
        var env = new ResourceEnvelope<JsonObject>(
            "/" + name,
            name,
            "Burbuja.Compute/workspaces",
            "local",
            new Dictionary<string, string>(),
            new JsonObject(),
            state,
            new SystemData(DateTimeOffset.UnixEpoch, null, null, null, null, null),
            "W/\"1\"");
        await store.UpsertAsync(env, null, TestContext.Current.CancellationToken);
    }

    private sealed class SpyHandler(RecoveryQuery? query) : IStreamRecoveryHandler
    {
        public List<string> Recovered { get; } = [];

        public string StreamType => "test";

        public RecoveryQuery? Query => query;

        public ValueTask RecoverAsync(string streamId, CancellationToken ct)
        {
            Recovered.Add(streamId);
            return default;
        }
    }
}
