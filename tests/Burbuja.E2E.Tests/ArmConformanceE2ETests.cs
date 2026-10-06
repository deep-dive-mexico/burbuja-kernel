// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using System.Net;
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>Group A: ARM conformance against the running Host (Mac child process or the Pi).</summary>
[Trait("Category", "E2E")]
public sealed class ArmConformanceE2ETests(SharedServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_api_version_returns_400()
    {
        using var client = server.Client();
        using var response = await client.Http.GetAsync("/subscriptions/local/resourceGroups/dev", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await ArmTestClient.ReadJsonAsync(response, Ct);
        body!["error"]!["code"]!.GetValue<string>().ShouldBe("MissingApiVersionParameter");
    }

    [Fact]
    public async Task Unauthorized_returns_401_with_www_authenticate()
    {
        using var client = ArmTestClient.Create(server.BaseUrl, apiKey: "");
        client.Http.DefaultRequestHeaders.Remove("x-api-key");
        using var response = await client.GetAsync("/subscriptions/local", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Reader_write_returns_403()
    {
        using var client = server.Client(SharedServerFixture.ReaderKey);
        using var response = await client.PutAsync(
            $"/subscriptions/local/resourceGroups/{server.NewName("rg")}",
            """{"properties":{}}""",
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stale_etag_returns_412()
    {
        using var client = server.Client();
        var name = server.NewName("rg");
        var path = $"/subscriptions/local/resourceGroups/{name}";

        // A distinct x-ms-client-request-id keeps the second write off the idempotency-replay path
        // (an identical retry is legitimately answered with the first 201).
        using var create = new HttpRequestMessage(HttpMethod.Put, ArmTestClient.Versioned(path))
        {
            Content = new StringContent("""{"properties":{}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        create.Headers.TryAddWithoutValidation("x-ms-client-request-id", Guid.NewGuid().ToString("N"));
        using var created = await client.Http.SendAsync(create, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var stale = new HttpRequestMessage(HttpMethod.Put, ArmTestClient.Versioned(path))
        {
            Content = new StringContent("""{"properties":{}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        stale.Headers.TryAddWithoutValidation("x-ms-client-request-id", Guid.NewGuid().ToString("N"));
        stale.Headers.TryAddWithoutValidation("If-Match", "W/\"999\"");
        using var response = await client.Http.SendAsync(stale, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Every_arm_response_has_request_id()
    {
        using var client = server.Client();
        using var ok = await client.GetAsync("/subscriptions/local", Ct);
        ok.Headers.Contains("x-ms-request-id").ShouldBeTrue();

        using var bad = await client.Http.GetAsync("/subscriptions/local/resourceGroups/dev", Ct);
        bad.Headers.Contains("x-ms-request-id").ShouldBeTrue();
    }

    [Fact]
    public async Task Providers_lists_namespaces_without_system_type()
    {
        using var client = server.Client();
        using var response = await client.GetAsync("/providers", Ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain("System.Type");

        var body = await ArmTestClient.ReadJsonAsync(response, Ct);
        var namespaces = body!["value"]!.AsArray()
            .Select(v => v!["name"]!.GetValue<string>())
            .ToList();
        namespaces.ShouldContain("Burbuja.Resources");
        namespaces.ShouldContain("Burbuja.Authorization");
        namespaces.ShouldContain("Burbuja.Deployments");
        namespaces.ShouldContain("Burbuja.Agents");
    }

    [Fact]
    public async Task Health_probes_need_no_key_or_api_version()
    {
        using var client = ArmTestClient.Create(server.BaseUrl, apiKey: "");
        client.Http.DefaultRequestHeaders.Remove("x-api-key");
        foreach (var path in new[] { "/health/live", "/health/ready", "/health/plugins" })
        {
            using var response = await client.Http.GetAsync(path, Ct);
            response.EnsureSuccessStatusCode();
        }

        // Track 3a: /health/plugins is now one JSON document with one entry per loaded plugin. Every
        // plugin this stack staged must be in it, by manifest id. The compose lane records the same
        // plugin set in stack-ready.json — as DIRECTORY names, because that is what `ls /app/plugins`
        // gives — and it writes that file only after this suite has finished, so the expected ids are
        // read from each staged plugin.json here (the same directory -> id mapping the kernel makes).
        using var health = await client.Http.GetAsync("/health/plugins", Ct);
        var body = await ArmTestClient.ReadJsonAsync(health, Ct);
        var reported = body!["plugins"]!.AsArray()
            .Select(p => p!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        reported.ShouldNotBeEmpty();

        var staged = StagedPluginIds(server.PluginRoot);
        staged.ShouldNotBeEmpty("the fixture stages every plugin before the run");
        foreach (var id in staged)
            reported.Contains(id).ShouldBeTrue($"plugin '{id}' is staged but missing from /health/plugins ({string.Join(",", reported)})");
    }

    /// <summary>Manifest ids of the staged plugin directories (a directory name is not the plugin id).</summary>
    private static IReadOnlyList<string> StagedPluginIds(string pluginRoot)
    {
        if (!Directory.Exists(pluginRoot))
            return [];
        return Directory.GetDirectories(pluginRoot)
            .Select(dir => Path.Combine(dir, "plugin.json"))
            .Where(File.Exists)
            .Select(json => JsonNode.Parse(File.ReadAllText(json))!["id"]!.GetValue<string>())
            .ToList();
    }

    [Fact]
    public async Task Sqlite_stores_are_active()
    {
        await Task.CompletedTask;
        if (server.IsRemote)
            return; // the remote lane does not expose its filesystem
        server.DatabasePath.ShouldNotBeNull();
        File.Exists(server.DatabasePath!).ShouldBeTrue("Store.Sqlite should have created the database");
    }
}
