using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Burbuja.Hosting;
using Example.Workbench.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Burbuja.Hosting.Tests;

/// <summary>Real Hosting composition and loader; only the HTTP transport is in-memory.</summary>
internal sealed class WorkbenchFixture : IAsyncDisposable
{
    public const string Namespace = "Example.Workbench";
    public const string Api = "?api-version=2026-09-01";
    public const string Scope = "/subscriptions/workbench/resourceGroups/demo";
    public const string Notes = Scope + "/providers/Example.Workbench/notes";
    public const string Owner = "workbench-owner-dev-key";
    public const string Reader = "workbench-reader-dev-key";
    public const string Editor = "workbench-editor-dev-key";
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkbenchFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "burbuja-workbench-tests", Guid.NewGuid().ToString("N"));
        PluginDirectory = Path.Combine(Root, "plugins", Namespace);
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "plugin-fixture"), PluginDirectory);
    }

    public string Root { get; }
    public string PluginDirectory { get; }
    public WebApplication App { get; private set; } = null!;

    public WebApplication Create(string role = "control-plane", bool enabled = true)
    {
        App = BurbujaApplication.CreateApp(new WebApplicationOptions
        {
            ApplicationName = typeof(WorkbenchHost).Assembly.GetName().Name,
            ContentRootPath = Root,
            EnvironmentName = "Production",
        }, WorkbenchHost.Options(builder =>
        {
            // Use the consumer's real config, with no ambient repo appsettings or environment aliases.
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "host-fixture", "appsettings.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kernel:PluginsDirectory"] = Path.Combine(Root, "plugins"),
                ["Kernel:Role"] = role,
                ["Plugins:Example.Workbench:Enabled"] = enabled.ToString(),
                ["Workspace:WorktreeRoot"] = Path.Combine(Root, "worktrees"),
                ["Workspace:RepoRoot"] = Root,
                ["Auth:ApiKeys:3:Key"] = "workbench-nobody-dev-key",
                ["Auth:ApiKeys:3:PrincipalId"] = "nobody",
                ["Logging:LogLevel:Default"] = "Error",
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        }));
        return App;
    }

    public async Task StartAsync(string role = "control-plane", bool enabled = true)
    {
        Create(role, enabled);
        await App.StartAsync(Ct);
    }

    public HttpClient Client(string? key = Owner)
    {
        var client = App.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        if (key is not null)
            client.DefaultRequestHeaders.Add("x-api-key", key);
        return client;
    }

    public async Task BootstrapAsync()
    {
        using var owner = Client();
        using var subscription = await owner.PutAsJsonAsync("/subscriptions/workbench" + Api,
            new { properties = new { displayName = "Workbench" } }, Ct);
        await ExpectAsync(subscription, HttpStatusCode.Created);
        using var group = await owner.PutAsJsonAsync(Scope + Api, new { location = "local", properties = new { } }, Ct);
        await ExpectAsync(group, HttpStatusCode.Created);
    }

    public static async Task<JsonObject> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
        return string.IsNullOrEmpty(body) ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
    }

    public static async Task<JsonArray> EventsAsync(HttpClient client, string resource)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, resource + "/events" + Api);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, Ct);
        return (await ExpectAsync(response, HttpStatusCode.OK))["value"]!.AsArray();
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null)
            await App.DisposeAsync();
        Directory.Delete(Root, recursive: true);
    }
}
