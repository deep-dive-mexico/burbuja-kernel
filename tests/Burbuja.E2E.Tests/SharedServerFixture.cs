using Burbuja.TestKit;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>Only the neutral published kernel and selected SQLite plugin; no product configuration, executable or assembly.</summary>
public sealed class SharedServerFixture : IAsyncLifetime
{
    private KernelTestHost _host = null!;
    private readonly List<KernelTestHost> _templates = [];
    private readonly List<int> _ports = [];
    public const string ApiKey = KernelTestHost.Owner;
    public const string ReaderKey = KernelTestHost.Reader;
    public const string McpKey = "kernel-e2e-mcp";
    public const string Lane = "kernel-e2e";
    public bool IsRemote => false;
    public string BaseUrl => _host.BaseUrl;
    public string PluginRoot => Path.Combine(_host.Root, "plugins");
    public string DatabasePath => Path.Combine(_host.Root, "state.db");
    public string ComputeRoot => Path.Combine(_host.Root, "compute");

    public async ValueTask InitializeAsync()
    {
        _host = new KernelTestHost();
        await _host.StartAsync();
        using var client = Client();
        await client.SeedAsync(ct: KernelTestHost.Ct);
    }

    public ArmTestClient Client(string? key = null) => ArmTestClient.Create(BaseUrl, key ?? ApiKey);
    public string NewName(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];
    public static string PublishKernelHost() => KernelTestHost.PublishedDirectory;

    public Task<ServerProcess> StartDedicatedOnFreePortAsync(string databasePath, string logName, CancellationToken ct)
    {
        var port = FreePort.Pick();
        _ports.Add(port);
        return StartDedicatedAsync(databasePath, port, logName, ct);
    }

    public async Task<ServerProcess> StartDedicatedAsync(string databasePath, int port, string logName, CancellationToken ct)
    {
        var template = new KernelTestHost(new Dictionary<string, string?>
        {
            ["Sqlite:Path"] = databasePath,
            ["Sqlite:ArtifactRoot"] = Path.Combine(Path.GetDirectoryName(databasePath)!, "artifacts"),
        });
        _templates.Add(template);
        return await ServerProcess.StartAsync(PublishKernelHost(), template.Settings,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["BURBUJA_SCRIPTED_SCENARIO_DIR"] = Path.Combine(KernelTestHost.Repository, "tests", "scenarios"),
            }, port: port,
            logPath: Path.Combine(KernelTestHost.Repository, "artifacts", "e2e-logs", Guid.NewGuid().ToString("N") + "-" + logName),
            repositoryRoot: KernelTestHost.Repository, ct: ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var template in _templates)
            await template.DisposeAsync();
        foreach (var port in _ports)
            FreePort.Release(port);
        await _host.DisposeAsync();
    }
}
