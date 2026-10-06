using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
[assembly: AssemblyFixture(typeof(Burbuja.E2E.Tests.SharedServerFixture))]

namespace Burbuja.E2E.Tests;

/// <summary>A real neutral executable, private SQLite state and public HTTP client; no product source or model credentials.</summary>
internal sealed class KernelTestHost : IAsyncDisposable
{
    public const string Api = "?api-version=2026-09-01";
    public const string Scope = "/subscriptions/e2e/resourceGroups/tests";
    public const string AgentRoot = Scope + "/providers/Burbuja.Agents";
    public const string Workspace = AgentRoot + "/workspaces/work";
    public const string IntegrationRoot = Scope + "/providers/Burbuja.Integrations/integrations";
    public const string Owner = "kernel-e2e-owner";
    public const string Reader = "kernel-e2e-reader";
    public const string Nobody = "kernel-e2e-nobody";
    private static readonly Lazy<string> Published = new(() => HostPublisher.EnsurePublished(
        sourceRoot: Repository, configuration: "Release"));
    public static string Repository => RepoLayout.FindRoot(
        startDirectory: AppContext.BaseDirectory, solutionFileName: "Burbuja.slnx");
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Dictionary<string, string?> _settings;
    private ServerProcess? _server;
    private readonly int _port = FreePort.Pick();

    public KernelTestHost(Dictionary<string, string?>? settings = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "burbuja-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var plugins = Path.Combine(Root, "plugins");
        var sqlite = PluginStager.FindBuiltDirectory(Path.Combine(Repository, "plugins", "Burbuja.Plugins.Store.Sqlite"))
            ?? throw new InvalidOperationException("Build Store.Sqlite before running the E2E suite.");
        Copy(sqlite, Path.Combine(plugins, "Store.Sqlite"));
        _settings = new()
        {
            ["Kernel:Role"] = "all",
            ["Kernel:NodeId"] = "kernel-e2e",
            ["Kernel:Seed"] = "false",
            ["Kernel:PluginsDirectory"] = plugins,
            ["Portal:BaseUrl"] = $"http://127.0.0.1:{_port}",
            ["Portal:SubscriptionId"] = "e2e",
            ["Portal:ResourceGroup"] = "tests",
            ["Kernel:TrustedPlugins:0"] = "Burbuja.Agents",
            ["Kernel:TrustedPlugins:1"] = "Policy.Core",
            ["Kernel:TrustedPlugins:2"] = "Supervisor",
            ["Kernel:TrustedPlugins:3"] = "Tools.Agents",
            ["Kernel:TrustedPlugins:4"] = "Tools.Workspace",
            ["Kernel:TrustedPlugins:5"] = "Tools.Http",
            ["Sqlite:Path"] = Path.Combine(Root, "state.db"),
            ["Sqlite:ArtifactRoot"] = Path.Combine(Root, "artifacts"),
            ["Compute:LocalDirectory:Root"] = Path.Combine(Root, "compute"),
            ["Auth:ApiKeys:0:Key"] = Owner,
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Auth:ApiKeys:1:Key"] = Reader,
            ["Auth:ApiKeys:1:PrincipalId"] = "reader",
            ["Auth:ApiKeys:1:Roles:0"] = "Reader",
            ["Auth:ApiKeys:2:Key"] = Nobody,
            ["Auth:ApiKeys:2:PrincipalId"] = "nobody",
            ["Auth:ApiKeys:3:Key"] = "kernel-e2e-mcp",
            ["Auth:ApiKeys:3:PrincipalId"] = "mcp-e2e",
            ["Auth:ApiKeys:3:PrincipalType"] = "Service",
            ["Auth:ApiKeys:3:Roles:0"] = "MCP Tool Caller",
            ["Auth:Users:0:Username"] = "owner",
            ["Auth:Users:0:PasswordHash"] = "pbkdf2-sha256:100000:hC1BoFmZjz4EWl1tz4dPDg==:EHvkQ52vSIt2nSVpDV7VswiA5frM3o7DzBu4a7OEKiI=",
            ["Auth:Users:0:PrincipalId"] = "owner",
            ["Auth:Users:1:Username"] = "reader",
            ["Auth:Users:1:PasswordHash"] = "pbkdf2-sha256:100000:hC1BoFmZjz4EWl1tz4dPDg==:EHvkQ52vSIt2nSVpDV7VswiA5frM3o7DzBu4a7OEKiI=",
            ["Auth:Users:1:PrincipalId"] = "reader",
            ["Agents:ProviderId"] = "scripted",
            ["Agents:ScenarioDirectory"] = Path.Combine(Repository, "tests", "scenarios"),
            ["Runtime:OutboxRelayInterval"] = "00:00:00.050",
            ["Runtime:RecoverySweepInterval"] = "00:00:00.250",
            ["Runtime:LeaseTtl"] = "00:00:02",
            ["Runtime:AgentApprovalWait"] = "00:00:00.100",
            ["Policy:DeferAfter"] = "00:00:00.100",
            ["Budget:DailyHardStopUsd"] = "1000",
            ["Arm:Throttle:WritesPerMinute"] = "12000",
            ["Arm:Throttle:ReadsPerMinute"] = "12000",
            ["Arm:Throttle:DeletesPerMinute"] = "12000",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in settings ?? [])
            _settings[key] = value;
    }

    public string Root { get; }
    internal IReadOnlyDictionary<string, string?> Settings => _settings;
    internal static string PublishedDirectory => Published.Value;
    public string BaseUrl => _server!.BaseUrl;
    public string LogPath => _server!.LogPath;

    public async Task StartAsync(bool bootstrap = true)
    {
        _server = await ServerProcess.StartAsync(Published.Value, _settings,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["BURBUJA_SCRIPTED_SCENARIO_DIR"] = Path.Combine(Repository, "tests", "scenarios"),
            },
            port: _port, repositoryRoot: Repository, ct: Ct,
            logPath: Path.Combine(Repository, "artifacts", "e2e-logs", Path.GetFileName(Root) + ".log"));
        if (bootstrap)
        {
            using var client = Client();
            await ExpectAsync(await client.PutAsJsonAsync("/subscriptions/e2e" + Api,
                new { properties = new { displayName = "E2E" } }, Ct), HttpStatusCode.Created);
            await ExpectAsync(await client.PutAsJsonAsync(Scope + Api,
                new { location = "local", properties = new { } }, Ct), HttpStatusCode.Created);
            await ExpectAsync(await client.PutAsJsonAsync(Workspace + Api,
                new { properties = new { displayName = "Work", profile = "DevFullAuto" } }, Ct), HttpStatusCode.Created);
        }
    }

    public HttpClient Client(string? key = Owner)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        if (key is not null)
            client.DefaultRequestHeaders.Add("x-api-key", key);
        return client;
    }

    public async Task KillAsync()
    {
        await _server!.StopAsync(hard: true);
        await _server.DisposeAsync();
        _server = null;
    }

    public static async Task<JsonObject> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status,
                $"Expected {(int)status}; got {(int)response.StatusCode}: {body}");
            return string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
        }
    }

    public static async Task<JsonObject> GetAsync(HttpClient client, string path) =>
        await ExpectAsync(await client.GetAsync(path + Api, Ct), HttpStatusCode.OK);

    public static async Task<JsonObject> WaitAsync(HttpClient client, string path,
        Func<JsonObject, bool> ready, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        JsonObject? last = null;
        try
        {
            while (true)
            {
                last = await GetAsync(client, path);
                if (ready(last))
                    return last;
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No expected state at {path}: {last}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        FreePort.Release(_port);
        Directory.Delete(Root, recursive: true);
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
