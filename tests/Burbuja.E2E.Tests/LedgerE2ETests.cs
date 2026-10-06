// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using System.Net;
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>
/// M5.1 slice C: the budget ledger is durable. A job whose own priced model books cost through the
/// SQLite-backed resource store, and a hard kill plus a restart on the same file must still show
/// that day's total through the read-only `ledgers` projection. It owns its Host process (its own
/// free port, its own SQLite file), so M6.0 gives it its own collection — the single `Disruptive`
/// collection was serialising two facts that already could not collide.
/// </summary>
[Collection("Disruptive-Ledger")]
[Trait("Category", "E2E")]
// Owns its own Host (start/kill/restart), so it is a disruptive neighbour for the two facts with the
// tightest budgets: 37 shared-host facts pass, and adding this one makes D4/D11 miss theirs. Same
// reason the browser group has its own pass — a fact that changes the environment, not a wrong fact.
[Trait("OwnsHost", "true")]
public sealed class LedgerE2ETests(SharedServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ScenarioTag = "burbuja.io/scenario";
    private const string AgentsRoot = "/subscriptions/local/resourceGroups/dev/providers/Burbuja.Agents";

    /// <summary>Staged by the fixture's host, so the composite calculator can price it.</summary>
    private const string PricedModelId = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";

    [Fact]
    public async Task Ledger_total_for_today_survives_a_hard_kill_and_restart()
    {
        // Lane-scoped: see DurabilityE2ETests — these facts own a Host each and must not sit in a
        // directory a concurrent lane is also using.
        var stateDir = Path.Combine(RepoLayout.Root, "artifacts", $"{SharedServerFixture.Lane}-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var databasePath = Path.Combine(stateDir, "burbuja.db");
        var ws = server.NewName("ws");
        var job = server.NewName("job");
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        decimal workerUsd;

        // The first start claims a free port and keeps it: the restart below MUST land on the same
        // port so the client can reuse the address.
        var first = await server.StartDedicatedOnFreePortAsync(databasePath, "ledger-1.log", Ct);
        var port = first.Port;
        try
        {
            using var client = ArmTestClient.Create(first.BaseUrl, SharedServerFixture.ApiKey);
            await client.SeedAsync(ct: Ct);
            await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);

            var path = await ArmTestHelpers.PutJobAsync(client, ws, job, "happy-path", ArmTestHelpers.PricedModelId, null, Ct);
            (await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct)).ShouldBe("succeeded");

            using var get = await client.GetAsync(path, Ct);
            var body = await ArmTestClient.ReadJsonAsync(get, Ct);
            workerUsd = body!["properties"]!["cost"]!["workerUsd"]!.GetValue<decimal>();
            workerUsd.ShouldBeGreaterThan(0, "a priced scripted usage must book real, non-zero cost");

            (await LedgerTotalAsync(client, day)).ShouldBeGreaterThanOrEqualTo(workerUsd);
            (await LedgerDaysAsync(client)).ShouldContain(day);
        }
        finally
        {
            await first.StopAsync(hard: true);
            await first.DisposeAsync();
        }

        File.Exists(databasePath).ShouldBeTrue("the SQLite file must exist after the first run");

        var second = await server.StartDedicatedAsync(databasePath, port, "ledger-2.log", Ct);
        try
        {
            using var client = ArmTestClient.Create(second.BaseUrl, SharedServerFixture.ApiKey);
            (await LedgerTotalAsync(client, day))
                .ShouldBeGreaterThanOrEqualTo(workerUsd, "the day's ledger total must survive the restart");
            (await LedgerDaysAsync(client)).ShouldContain(day);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- helpers
    //
    // M13 R0: the workspace/job recipes and the job wait moved to `ArmTestHelpers`, which was byte-identical
    // to what stood here. The one thing this fact needs that the shared helper defaults away from is the
    // PRICED model: the host's default model has no price book, and a job with no priced model is refused at
    // PUT — so the id is passed explicitly rather than assumed.

    private static async Task<decimal> LedgerTotalAsync(ArmTestClient client, string day)
    {
        using var get = await client.GetAsync(AgentsRoot + "/ledgers/" + day, Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK, await get.Content.ReadAsStringAsync(Ct));
        var body = await ArmTestClient.ReadJsonAsync(get, Ct);
        return body!["properties"]!["spentUsd"]!.GetValue<decimal>();
    }

    private static async Task<List<string>> LedgerDaysAsync(ArmTestClient client)
    {
        using var list = await client.GetAsync(AgentsRoot + "/ledgers", Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.Content.ReadAsStringAsync(Ct));
        var page = await ArmTestClient.ReadJsonAsync(list, Ct);
        return page!["value"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()).ToList();
    }
}
