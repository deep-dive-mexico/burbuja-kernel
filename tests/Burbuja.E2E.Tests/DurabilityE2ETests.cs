// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>
/// Group E: durability and boot failure. These own their Host process — each fact picks its own free
/// port and its own SQLite file — so M6.0 gives them their own collection instead of sharing one
/// `Disruptive` collection with the ledger facts: two facts that already cannot collide were being
/// serialised against each other for no reason.
/// </summary>
[Collection("Disruptive-Durability")]
[Trait("Category", "E2E")]
// Owns its own Host (start/kill/restart), so it is a disruptive neighbour for the two facts with the
// tightest budgets: 37 shared-host facts pass, and adding this one makes D4/D11 miss theirs. Same
// reason the browser group has its own pass — a fact that changes the environment, not a wrong fact.
[Trait("OwnsHost", "true")]
public sealed class DurabilityE2ETests(SharedServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resource_survives_a_hard_kill_and_restart_on_the_same_sqlite_file()
    {
        // Lane-scoped: two lanes can run this suite at once (verify-all fans them out), and
        // `artifacts/e2e-state` is also the shared self-hosted fixture's own directory.
        var stateDir = Path.Combine(RepoLayout.Root, "artifacts", $"{SharedServerFixture.Lane}-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var databasePath = Path.Combine(stateDir, "burbuja.db");

        var name = "rg-" + Guid.NewGuid().ToString("N")[..8];
        var path = $"/subscriptions/local/resourceGroups/{name}";

        // The first start claims a free port and keeps it: the restart below MUST land on the same
        // port so the client can reuse the address. The claim is kept for the test process's life, so
        // no neighbour can take the port in the kill→restart gap (see StartDedicatedOnFreePortAsync).
        var first = await server.StartDedicatedOnFreePortAsync(databasePath, "durability-1.log", Ct);
        var port = first.Port;
        string? etag;
        try
        {
            using var client = ArmTestClient.Create(first.BaseUrl, SharedServerFixture.ApiKey);
            await client.SeedAsync(ct: Ct);
            using var created = await client.PutAsync(path, """{"properties":{}}""", Ct);
            created.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
            etag = created.Headers.ETag?.Tag;
        }
        finally
        {
            await first.StopAsync(hard: true);
            await first.DisposeAsync();
        }

        File.Exists(databasePath).ShouldBeTrue("the SQLite file must exist after the first run");

        var second = await server.StartDedicatedAsync(databasePath, port, "durability-2.log", Ct);
        try
        {
            using var client = ArmTestClient.Create(second.BaseUrl, SharedServerFixture.ApiKey);
            using var read = await client.GetAsync(path, Ct);
            read.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            var body = await ArmTestClient.ReadJsonAsync(read, Ct);
            body!["name"]!.GetValue<string>().ShouldBe(name);
            if (etag is not null)
                read.Headers.ETag?.Tag.ShouldBe(etag);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Missing_plugin_assembly_exits_78_and_names_the_plugin()
    {
        var hostDir = SharedServerFixture.PublishKernelHost();
        var plugins = Path.Combine(RepoLayout.Root, "artifacts", "e2e-bad-plugins", Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(plugins, "Ghost");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"),
            """
            {
              "id": "Ghost.E2E",
              "name": "Ghost",
              "version": "1.0.0",
              "contracts_version": "1.0.0",
              "assembly": "Ghost.E2E.Missing.dll",
              "entry_type": "Ghost.E2E.Plugin",
              "load": "dynamic",
              "provides": [],
              "extension_points": [],
              "requires": [],
              "roles": ["all"],
              "config_section": null,
              "trusted": false,
              "depends_on": [],
              "load_order": 50
            }
            """,
            Ct);

        var result = await ProcessRunner.RunAsync(
            "dotnet",
            [
                Path.Combine(hostDir, "Burbuja.Host.dll"),
                "--role=all",
                $"--Kernel:PluginsDirectory={plugins}",
                "--Kernel:Seed=false",
                // M14 R5p — the port comes from this checkout's BAND, not from a literal. The fact is about
                // a plugin that refuses to load; 18399 was a number somebody typed, and it is inside the
                // `sources` lane's band, so a lane running this fact would either collide with its own Host
                // or be told the port is taken by a process it cannot see. Picking is a claim (see FreePort),
                // and the claim is released when the child exits.
                $"--Kestrel:Endpoints:Http:Url=http://127.0.0.1:{FreePort.Pick()}",
                "--Auth:ApiKeys:0:Key=k",
                "--Auth:ApiKeys:0:PrincipalId=p",
                "--Auth:ApiKeys:0:Roles:0=Owner",
            ],
            hostDir,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Production" },
            Ct);

        result.ExitCode.ShouldBe(78);
        result.StandardError.ShouldContain("Ghost.E2E");
    }
}
