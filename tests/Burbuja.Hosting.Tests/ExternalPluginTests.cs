using System.Net;
using System.Net.Http.Json;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Plugin;
using Burbuja.Portal;
using Example.Workbench.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Burbuja.Hosting.Tests.WorkbenchFixture;

namespace Burbuja.Hosting.Tests;

public sealed class ExternalPluginTests
{
    [Fact]
    public async Task Own_host_loads_a_contracts_only_provider_in_a_separate_context_and_serves_its_manifest()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();
        var loaded = Assert.Single(host.App.Services.GetRequiredService<PluginHost>().Loaded,
            p => p.Manifest.Id == Namespace);
        Assert.Equal(PluginLoadKind.Dynamic, loaded.EffectiveLoad);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.Assembly));
        Assert.Equal(Path.GetFullPath(Path.Combine(host.PluginDirectory, "Example.Workbench.Plugin.dll")), loaded.Assembly.Location);
        Assert.Contains(typeof(IBurbujaPlugin), loaded.Instance.GetType().GetInterfaces());
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "Example.Workbench.Plugin");

        var provider = Assert.Single(host.App.Services.GetServices<IResourceProvider>(), p => p.Namespace == Namespace);
        Assert.Same(loaded.Assembly, provider.GetType().Assembly);
        var references = loaded.Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.Equal(new[] { "Burbuja.Core.Abstractions", "Burbuja.Core.Sdk" },
            references.Where(n => n.StartsWith("Burbuja.", StringComparison.Ordinal)).Order().ToArray());
        Assert.Empty(Directory.EnumerateFiles(host.PluginDirectory, "Burbuja.*.dll"));

        // Inspect the actual compiled dependency closure, not the source of an unrelated executable.
        foreach (var name in new[] { "host-fixture/Example.Workbench.Host.deps.json", "Burbuja.Hosting.Tests.deps.json" })
        {
            var deps = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, name), Ct))!;
            var libraries = deps["libraries"]!.AsObject().Select(p => p.Key).ToArray();
            Assert.DoesNotContain(libraries, n => n.Contains("Akos", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(libraries, n => n.StartsWith("Example.Workbench.Plugin/", StringComparison.Ordinal));
            Assert.DoesNotContain(libraries, n => n.StartsWith("Burbuja.Host/", StringComparison.Ordinal));
        }

        using var client = host.Client();
        foreach (var path in new[] { "/health/live", "/health/ready", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path, Ct);
            response.EnsureSuccessStatusCode();
        }
        using var healthResponse = await client.GetAsync("/health/plugins", Ct);
        var health = await ExpectAsync(healthResponse, HttpStatusCode.OK);
        Assert.Single(health["plugins"]!.AsArray(), p => p!["id"]!.GetValue<string>() == Namespace);

        using var manifestResponse = await client.GetAsync("/providers/Example.Workbench" + Api, Ct);
        var manifest = await ExpectAsync(manifestResponse, HttpStatusCode.OK);
        var notes = Assert.Single(manifest["properties"]!["resourceTypes"]!.AsArray())!;
        Assert.Equal("notes", notes["resourceType"]!.GetValue<string>());
        Assert.Equal("2026-09-01", notes["defaultApiVersion"]!.GetValue<string>());
        Assert.Equal("events", Assert.Single(notes["dataPlaneRoutes"]!.AsArray())!["segment"]!.GetValue<string>());
        Assert.Equal(3, notes["rbacActions"]!.AsArray().Count);
        Assert.Contains("Example.Workbench/notes/write", host.App.Services.GetRequiredService<IAuthorizationEvaluator>().ActionCatalogue);
    }

    [Fact]
    public async Task Http_crud_preserves_etags_and_traces_only_successful_writes_with_isolated_verified_streams()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();
        await host.BootstrapAsync();
        using var editor = host.Client(Editor);
        const string resource = Notes + "/first";
        using var createdResponse = await editor.PutAsJsonAsync(resource + Api, new { properties = new { text = "first private text" } }, Ct);
        var created = await ExpectAsync(createdResponse, HttpStatusCode.Created);
        var etag = createdResponse.Headers.ETag!.ToString();
        Assert.Equal("editor", created["systemData"]!["createdBy"]!.GetValue<string>());
        Assert.Equal("Example.Workbench/notes", created["type"]!.GetValue<string>());

        // A repeated identical PUT is replayed by the kernel without entering the plugin again.
        using var replay = await editor.PutAsJsonAsync(resource + Api, new { properties = new { text = "first private text" } }, Ct);
        await ExpectAsync(replay, HttpStatusCode.Created);
        Assert.Single(await EventsAsync(editor, resource));

        using var update = new HttpRequestMessage(HttpMethod.Put, resource + Api)
        {
            Content = JsonContent.Create(new { properties = new { text = "second private text" } }),
        };
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updatedResponse = await editor.SendAsync(update, Ct);
        await ExpectAsync(updatedResponse, HttpStatusCode.OK);
        Assert.NotEqual(etag, updatedResponse.Headers.ETag!.ToString());

        using var stale = new HttpRequestMessage(HttpMethod.Put, resource + Api)
        {
            Content = JsonContent.Create(new { properties = new { text = "stale overwrite" } }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        using var staleResponse = await editor.SendAsync(stale, Ct);
        await ExpectAsync(staleResponse, HttpStatusCode.PreconditionFailed);

        using var read = await editor.GetAsync(resource + Api, Ct);
        Assert.Equal("second private text", (await ExpectAsync(read, HttpStatusCode.OK))["properties"]!["text"]!.GetValue<string>());
        using var other = await editor.PutAsJsonAsync(Notes + "/other" + Api, new { properties = new { text = "another stream" } }, Ct);
        await ExpectAsync(other, HttpStatusCode.Created);
        using var list = await editor.GetAsync(Notes + Api, Ct);
        Assert.Equal(2, (await ExpectAsync(list, HttpStatusCode.OK))["value"]!.AsArray().Count);

        var events = await EventsAsync(editor, resource);
        Assert.Equal(2, events.Count);
        Assert.All(events, item =>
        {
            Assert.Equal("workbench.note.saved", item!["type"]!.GetValue<string>());
            Assert.Equal(resource, item["payload"]!["resourceId"]!.GetValue<string>());
            Assert.Equal("editor", item["payload"]!["principalId"]!.GetValue<string>());
        });
        Assert.DoesNotContain("private text", events.ToJsonString());
        Assert.Single(await EventsAsync(editor, Notes + "/other"));
        using var verify = await editor.GetAsync(resource + "/events/verify" + Api, Ct);
        var chain = await ExpectAsync(verify, HttpStatusCode.OK);
        Assert.True(chain["verified"]!.GetValue<bool>());
        Assert.True(chain["complete"]!.GetValue<bool>());
        Assert.Equal(2, chain["events"]!.GetValue<int>());

        using var delete = await editor.DeleteAsync(resource + Api, Ct);
        await ExpectAsync(delete, HttpStatusCode.OK);
        using var missing = await editor.GetAsync(resource + Api, Ct);
        await ExpectAsync(missing, HttpStatusCode.NotFound);
        var deletedTrace = await EventsAsync(editor, resource);
        Assert.Equal(3, deletedTrace.Count);
        Assert.Equal("workbench.note.deleted", deletedTrace[^1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declared_event_route_replays_sse_after_the_clients_last_event_id()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();
        await host.BootstrapAsync();
        using var editor = host.Client(Editor);
        const string resource = Notes + "/stream";
        using var created = await editor.PutAsJsonAsync(resource + Api, new { properties = new { text = "first" } }, Ct);
        await ExpectAsync(created, HttpStatusCode.Created);
        using var updated = await editor.PutAsJsonAsync(resource + Api, new { properties = new { text = "second" } }, Ct);
        await ExpectAsync(updated, HttpStatusCode.OK);
        var events = await EventsAsync(editor, resource);
        Assert.Equal(2, events.Count);
        using var request = new HttpRequestMessage(HttpMethod.Get, resource + "/events" + Api);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", events[0]!["seq"]!.ToString());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await editor.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var frame = new List<string>();
        while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
            frame.Add(line);
        Assert.Contains("event: workbench.note.saved", frame);
        Assert.Contains("id: " + events[1]!["seq"]!.ToString(), frame);
        var payload = JsonNode.Parse(Assert.Single(frame, line => line.StartsWith("data: ", StringComparison.Ordinal))[6..])!;
        Assert.Equal(resource, payload["resourceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reader_cannot_write_or_read_the_data_plane_and_editor_cannot_administer_the_kernel()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();
        await host.BootstrapAsync();
        using var editor = host.Client(Editor);
        using var created = await editor.PutAsJsonAsync(Notes + "/rbac" + Api, new { properties = new { text = "keep" } }, Ct);
        await ExpectAsync(created, HttpStatusCode.Created);
        using var reader = host.Client(Reader);
        using var readable = await reader.GetAsync(Notes + "/rbac" + Api, Ct);
        await ExpectAsync(readable, HttpStatusCode.OK);
        using var list = await reader.GetAsync(Notes + Api, Ct);
        await ExpectAsync(list, HttpStatusCode.OK);

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, Notes + "/rbac" + Api);
            if (method == HttpMethod.Put)
                request.Content = JsonContent.Create(new { properties = new { text = "forbidden overwrite" } });
            using var response = await reader.SendAsync(request, Ct);
            var error = await ExpectAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal("AuthorizationFailed", error["error"]!["code"]!.GetValue<string>());
        }
        using var trace = await reader.GetAsync(Notes + "/rbac/events" + Api, Ct);
        await ExpectAsync(trace, HttpStatusCode.Forbidden);
        using var anonymous = host.Client(null);
        using var unauthorized = await anonymous.GetAsync(Notes + "/rbac" + Api, Ct);
        await ExpectAsync(unauthorized, HttpStatusCode.Unauthorized);
        using var nobody = host.Client("workbench-nobody-dev-key");
        using var noRole = await nobody.GetAsync(Notes + "/rbac" + Api, Ct);
        await ExpectAsync(noRole, HttpStatusCode.Forbidden);
        using var subscription = await editor.PutAsJsonAsync("/subscriptions/not-allowed" + Api,
            new { properties = new { displayName = "no" } }, Ct);
        await ExpectAsync(subscription, HttpStatusCode.Forbidden);

        using var kept = await reader.GetAsync(Notes + "/rbac" + Api, Ct);
        Assert.Equal("keep", (await ExpectAsync(kept, HttpStatusCode.OK))["properties"]!["text"]!.GetValue<string>());
        Assert.Single(await EventsAsync(editor, Notes + "/rbac"));
    }

    [Fact]
    public async Task Declared_schema_version_and_verbs_reject_bad_requests_before_the_handler()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();
        await host.BootstrapAsync();
        using var editor = host.Client(Editor);
        foreach (var body in new[] { "{}", "{\"text\":\"\"}", "{\"text\":2}", "{\"text\":\"ok\",\"extra\":true}",
                     "{\"text\":\"" + new string('x', 1001) + "\"}" })
        {
            using var response = await editor.PutAsync(Notes + "/invalid" + Api,
                new StringContent("{\"properties\":" + body + "}", Encoding.UTF8, "application/json"), Ct);
            await ExpectAsync(response, HttpStatusCode.BadRequest);
        }
        foreach (var suffix in new[] { "", "?api-version=1900-01-01" })
        {
            using var response = await editor.GetAsync(Notes + suffix, Ct);
            await ExpectAsync(response, HttpStatusCode.BadRequest);
        }
        using var created = await editor.PutAsJsonAsync(Notes + "/valid" + Api, new { properties = new { text = "valid" } }, Ct);
        await ExpectAsync(created, HttpStatusCode.Created);
        using var patch = await editor.PatchAsJsonAsync(Notes + "/valid" + Api, new { properties = new { text = "no patch" } }, Ct);
        await ExpectAsync(patch, HttpStatusCode.MethodNotAllowed);
        using var invalid = await editor.GetAsync(Notes + "/invalid" + Api, Ct);
        await ExpectAsync(invalid, HttpStatusCode.NotFound);
        Assert.Single(await EventsAsync(editor, Notes + "/valid"));
    }

    [Theory]
    [InlineData("missing-assembly", "missing assembly")]
    [InlineData("wrong-contract", "contracts_version")]
    [InlineData("wrong-entry", "entry_type")]
    public async Task Broken_external_install_fails_boot_with_the_plugin_named(string damage, string diagnostic)
    {
        await using var host = new WorkbenchFixture();
        var manifestPath = Path.Combine(host.PluginDirectory, "plugin.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, Ct))!.AsObject();
        if (damage == "missing-assembly")
            File.Delete(Path.Combine(host.PluginDirectory, "Example.Workbench.Plugin.dll"));
        else
        {
            manifest[damage == "wrong-contract" ? "contracts_version" : "entry_type"] = damage == "wrong-contract" ? "999.0.0" : "Example.DoesNotExist";
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(), Ct);
        }
        var error = Assert.Throws<PluginBootException>(() => host.Create());
        Assert.Contains(Namespace, error.Message);
        Assert.Contains(diagnostic, error.Message);
    }

    [Fact]
    public async Task Disabled_plugin_disappears_from_health_routes_and_roles_while_host_stays_ready()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync(enabled: false);
        Assert.DoesNotContain(host.App.Services.GetRequiredService<PluginHost>().Loaded, p => p.Manifest.Id == Namespace);
        Assert.DoesNotContain(host.App.Services.GetServices<IResourceProvider>(), p => p.Namespace == Namespace);
        Assert.DoesNotContain(host.App.Services.GetServices<IRoleContributor>().SelectMany(c => c.Roles), r => r.Name == "Workbench Reader");
        using var client = host.Client();
        using var health = await client.GetAsync("/health/ready", Ct);
        health.EnsureSuccessStatusCode();
        using var missing = await client.GetAsync("/providers/Example.Workbench" + Api, Ct);
        var error = await ExpectAsync(missing, HttpStatusCode.BadRequest);
        Assert.Equal("NoRegisteredProviderFound", error["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Worker_loads_an_all_roles_plugin_but_does_not_expose_control_plane_http_surfaces()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync(role: "worker");
        Assert.Single(host.App.Services.GetRequiredService<PluginHost>().Loaded, p => p.Manifest.Id == Namespace);
        using var client = host.Client();
        using var health = await client.GetAsync("/health/ready", Ct);
        health.EnsureSuccessStatusCode();
        foreach (var path in new[] { "/", "/providers" + Api, "/portal/signin", "/workbench", "/openapi/v1.json", "/mcp" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Consumer_portal_uses_its_own_assembly_and_real_cookie_policy_without_kernel_portal_access()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();
        var registry = host.App.Services.GetRequiredService<PortalRegistry>();
        Assert.Equal(new[] { "kernel", "workbench" }, registry.All.Select(s => s.Id));
        Assert.Same(typeof(WorkbenchHost).Assembly, Assert.Single(registry.AdditionalAssemblies));
        using var browser = host.Client(null);
        using var landing = await browser.GetAsync("/", Ct);
        Assert.Equal("/workbench", landing.Headers.Location!.OriginalString);
        using var guarded = await browser.GetAsync("/workbench", Ct);
        Assert.Equal(HttpStatusCode.Found, guarded.StatusCode);
        Assert.StartsWith("/workbench/signin", guarded.Headers.Location!.OriginalString);
        using var form = await browser.GetAsync("/workbench/signin", Ct);
        form.EnsureSuccessStatusCode();
        Assert.Contains("Workbench sign in", await form.Content.ReadAsStringAsync(Ct));

        var credentials = new Dictionary<string, string> { ["username"] = "owner", ["password"] = "owner-dev-password" };
        using var wrongDoor = await browser.PostAsync("/portal/signin", new FormUrlEncodedContent(credentials), Ct);
        Assert.Equal("/portal/signin?error=noaccess", wrongDoor.Headers.Location!.OriginalString);
        Assert.False(wrongDoor.Headers.Contains("Set-Cookie"));
        credentials["returnUrl"] = "/portal";
        using var login = await browser.PostAsync("/workbench/signin", new FormUrlEncodedContent(credentials), Ct);
        Assert.Equal("/workbench", login.Headers.Location!.OriginalString);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Owner, cookie);
        browser.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var home = await browser.GetAsync("/workbench", Ct);
        home.EnsureSuccessStatusCode();
        Assert.Contains("Its notes provider is installed as a separate plugin", await home.Content.ReadAsStringAsync(Ct));
        using var console = await browser.GetAsync("/portal", Ct);
        Assert.Equal(HttpStatusCode.Found, console.StatusCode);
        using var signout = await browser.PostAsync("/workbench/signout", new FormUrlEncodedContent([]), Ct);
        Assert.Equal("/workbench/signin", signout.Headers.Location!.OriginalString);
        using var ended = await browser.GetAsync("/workbench", Ct);
        Assert.Equal(HttpStatusCode.Found, ended.StatusCode);
    }
}
