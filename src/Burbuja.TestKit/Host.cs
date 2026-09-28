using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Burbuja.TestKit;

/// <summary>Publishes the Host into a folder that survives across a test run, and republishes when the tree moved.</summary>
public static class HostPublisher
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Publishes a host project. Paths may be absolute or relative to sourceRoot. An explicit project uses
    /// sourceRoot (or RepoLayout.Root) and its own output folder; omitting it keeps the historical kernel host.
    /// entryAssemblyName is the published DLL filename, including .dll. The cache also keys on configuration.
    /// </summary>
    public static string EnsurePublished(
        string? directory = null,
        string configuration = "Release",
        CancellationToken ct = default,
        string? projectPath = null,
        string entryAssemblyName = "Burbuja.Host.dll",
        string? sourceRoot = null)
    {
        ValidateEntryAssembly(entryAssemblyName);
        var root = Path.GetFullPath(sourceRoot ?? (projectPath is null ? RepoLayout.KernelRoot : RepoLayout.Root));
        var project = Path.GetFullPath(projectPath ?? Path.Combine("src", "Burbuja.Host", "Burbuja.Host.csproj"), root);
        var defaultTarget = projectPath is null && entryAssemblyName == "Burbuja.Host.dll"
            ? Path.Combine(root, "artifacts", "hostonce")
            : Path.Combine(root, "artifacts", "hostonce", Path.GetFileNameWithoutExtension(entryAssemblyName), configuration);
        var target = Path.GetFullPath(directory
            ?? Environment.GetEnvironmentVariable("BURBUJA_E2E_HOST_DIR") ?? defaultTarget, root);
        var dll = Path.Combine(target, entryAssemblyName);
        var identity = new PublishIdentity(project, entryAssemblyName, configuration, root);
        var stampPath = Path.Combine(target, ".testkit-publish.json");
        var stamp = JsonSerializer.Serialize(identity);
        bool Reusable()
        {
            try
            {
                return !IsStale(target, root, entryAssemblyName, project)
                    && File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp;
            }
            catch (IOException)
            {
                // Another publisher can invalidate the stamp between Exists and ReadAllText.
                return false;
            }
        }
        // X45 (M16 M-R3) — the published Host was cached forever: `EnsurePublished` returned as soon as
        // `Burbuja.Host.dll` existed, so a browser fact could measure a binary built before the tree under
        // test. Measured in lane K: `G16` still read the M15 rail nineteen minutes after M-R1 removed it, and
        // the red read exactly like a product regression. The cheap check outside the gate answers the common
        // case; the same check inside it answers the second caller while the first is still publishing.
        if (Reusable())
            return target;

        Gate.Wait(ct);
        try
        {
            if (Reusable())
                return target;
            Directory.CreateDirectory(target);
            // A failed publish must never leave an old success stamp next to partly replaced output.
            File.Delete(stampPath);
            File.Delete(dll);
            var result = ProcessRunner.RunAsync(
                "dotnet",
                ["publish", project, "-c", configuration, "-o", target, "-m:1", "--disable-build-servers"],
                root,
                null,
                ct).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || !File.Exists(dll))
                throw new InvalidOperationException($"dotnet publish failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");
            File.WriteAllText(stampPath, stamp);
            return target;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// True when <paramref name="target"/>'s Host assembly is missing or older than the newest file a
    /// <c>dotnet publish</c> of it reads.
    ///
    /// <para><b>X45.</b> The fingerprint is the assembly's write time against <c>src/**</c>, <c>plugins/**</c>,
    /// the explicit project's directory and the repository's build files (build/publish output excluded).
    /// A content hash would be stronger, but the mtime
    /// comparison is what the measured failure needs and it costs one directory walk; a publish that is
    /// newer than every input is the only case that returns false, so the dangerous direction (a stale
    /// binary used silently) always republishes.</para>
    /// </summary>
    public static bool IsStale(string target, string? sourceRoot = null, string entryAssemblyName = "Burbuja.Host.dll", string? projectPath = null)
    {
        ValidateEntryAssembly(entryAssemblyName);
        var dll = Path.Combine(target, entryAssemblyName);
        if (!File.Exists(dll))
            return true;
        var root = string.IsNullOrEmpty(sourceRoot) ? RepoLayout.KernelRoot : sourceRoot;
        return NewestInputUtc(root, projectPath, Path.GetFullPath(target)) > File.GetLastWriteTimeUtc(dll);
    }

    /// <summary>The newest write time among the files the Host's publish reads.</summary>
    public static DateTime NewestInputUtc(string root) => NewestInputUtc(root, null, null);

    private static DateTime NewestInputUtc(string root, string? projectPath, string? publishDirectory)
    {
        var newest = DateTime.MinValue;
        void Consider(string path)
        {
            var at = File.GetLastWriteTimeUtc(path);
            if (at > newest)
                newest = at;
        }

        foreach (var name in InputRootFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                Consider(path);
        }

        var sources = new List<string> { Path.Combine(root, "src"), Path.Combine(root, "plugins") };
        if (projectPath is not null)
            sources.Add(Path.GetDirectoryName(Path.GetFullPath(projectPath, Path.GetFullPath(root)))!);
        foreach (var source in sources.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(source))
                continue;
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(path) || (publishDirectory is not null
                    && Path.GetFullPath(path).StartsWith(publishDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    continue;
                Consider(path);
            }
        }

        return newest;
    }

    private static readonly string[] InputRootFiles =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "nuget.config",
    ];

    private sealed record PublishIdentity(string ProjectPath, string EntryAssemblyName, string Configuration, string SourceRoot);

    internal static void ValidateEntryAssembly(string entryAssemblyName)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyName)
            || Path.GetFileName(entryAssemblyName) != entryAssemblyName
            || !entryAssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("entry assembly must be a DLL filename, including .dll", nameof(entryAssemblyName));
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}

/// <summary>Copies each plugin's `dotnet build` output (never `publish`, never `obj`) into one staged root.</summary>
public static class PluginStager
{
    public static IReadOnlyList<string> StageAll(string pluginsRoot, string destination)
    {
        Directory.CreateDirectory(destination);
        var staged = new List<string>();
        foreach (var pluginDir in Directory.GetDirectories(pluginsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(pluginDir, "plugin.json")))
                continue;
            var built = FindBuiltDirectory(pluginDir)
                ?? throw new InvalidOperationException($"plugin '{Path.GetFileName(pluginDir)}' has no net10.0 build output");
            var target = Path.Combine(destination, Path.GetFileName(pluginDir));
            CopyDirectory(built, target, Guard);
            staged.Add(Path.GetFileName(pluginDir));
        }

        return staged;
    }

    private static void Guard(string file)
    {
        var name = Path.GetFileName(file);
        if (name is "Burbuja.Core.dll" or "Burbuja.Core.Sdk.dll" or "Burbuja.Core.Abstractions.dll")
            throw new InvalidOperationException($"FATAL: {name} must not be copied beside a plugin (ALC unification)");
    }

    public static string? FindBuiltDirectory(string pluginDir)
    {
        var bin = Path.Combine(pluginDir, "bin");
        if (!Directory.Exists(bin))
            return null;
        return Directory.GetDirectories(bin, "net10.0", SearchOption.AllDirectories)
            .Where(d => !d.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !d.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void CopyDirectory(string source, string destination, Action<string> guard)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;
            guard(file);
            File.Copy(file, Path.Combine(destination, name), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "publish" or "obj")
                continue;
            CopyDirectory(dir, Path.Combine(destination, name), guard);
        }
    }
}

/// <summary>A published Host running as a child process on a loopback port.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly bool _ownsPortClaim;

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string LogPath { get; }

    private ServerProcess(Process process, int port, string logPath, bool ownsPortClaim)
    {
        _process = process;
        Port = port;
        LogPath = logPath;
        _ownsPortClaim = ownsPortClaim;
    }

    public static async Task<ServerProcess> StartAsync(
        string hostDirectory,
        IReadOnlyDictionary<string, string?> settings,
        IReadOnlyDictionary<string, string?> environment,
        int? port = null,
        string? logPath = null,
        TimeSpan? readyTimeout = null,
        CancellationToken ct = default,
        string entryAssemblyName = "Burbuja.Host.dll",
        string? repositoryRoot = null)
    {
        HostPublisher.ValidateEntryAssembly(entryAssemblyName);
        hostDirectory = Path.GetFullPath(hostDirectory);
        var dll = Path.Combine(hostDirectory, entryAssemblyName);
        if (!File.Exists(dll))
            throw new FileNotFoundException("published host entry assembly not found", dll);
        var root = repositoryRoot ?? (logPath is null ? RepoLayout.Root : null);
        var chosen = port ?? FreePort.Pick();
        var args = new List<string> { dll };
        foreach (var (key, value) in settings)
            args.Add($"--{key}={value}");
        args.Add($"--Kestrel:Endpoints:Http:Url=http://127.0.0.1:{chosen}");

        var log = logPath ?? Path.Combine(root!, "artifacts", "e2e-logs", $"host-{chosen}.log");
        ServerProcess? server = null;
        try
        {
            var process = ProcessRunner.Start("dotnet", args, hostDirectory, environment, log);
            server = new ServerProcess(process, chosen, log, ownsPortClaim: port is null);
            await server.WaitForReadyAsync(readyTimeout ?? TimeSpan.FromSeconds(60), ct);
            return server;
        }
        catch
        {
            if (server is not null)
                await server.DisposeAsync();
            else if (port is null)
                FreePort.Release(chosen);
            throw;
        }
    }

    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"Host exited early with code {_process.ExitCode}. Log: {LogPath}");
            try
            {
                using var resp = await http.GetAsync("/health/ready", ct);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException($"Host did not become ready within {timeout}. Log: {LogPath}");
    }

    public async Task<int> StopAsync(bool hard = false)
    {
        if (_process.HasExited)
            return _process.ExitCode;
        if (hard)
            _process.Kill(entireProcessTree: true);
        else
        {
            _process.CloseMainWindow();
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync();
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        _process.Dispose();
        if (_ownsPortClaim)
            FreePort.Release(Port);
    }
}

/// <summary>Raw ARM HTTP helpers for black-box tests (api-version + x-api-key).</summary>
public sealed class ArmTestClient : IDisposable
{
    public const string ApiVersion = "2026-09-01";

    private readonly HttpClient _http;

    private ArmTestClient(HttpClient http) => _http = http;

    public HttpClient Http => _http;

    public static ArmTestClient Create(string baseUrl, string apiKey = "local-dev-api-key")
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        return new ArmTestClient(http);
    }

    public static string Versioned(string path) =>
        path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + $"api-version={ApiVersion}";

    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct = default) => _http.GetAsync(Versioned(path), ct);

    public Task<HttpResponseMessage> PutAsync(string path, string json, CancellationToken ct = default) => _http.PutAsync(Versioned(path), Json(json), ct);

    public Task<HttpResponseMessage> DeleteAsync(string path, CancellationToken ct = default) => _http.DeleteAsync(Versioned(path), ct);

    /// <summary>
    /// PATCH, which the kernel supports and this client did not. Added when group P needed the same partial
    /// write the portal's own pause button sends: a fact that reached for a PUT to change one field would be
    /// testing a different request than the page makes, and the ETag semantics differ.
    /// </summary>
    public Task<HttpResponseMessage> PatchAsync(string path, string json, CancellationToken ct = default) =>
        _http.PatchAsync(Versioned(path), Json(json), ct);

    /// <summary>
    /// A POST carrying <c>If-Match</c> — an action with a precondition. Actions are preconditions like any
    /// other request (the concurrency gate covers them), and <see cref="HttpClient"/>'s convenience POST
    /// cannot set the header, so a lane that wants to assert the refusal needs this.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(string path, string? json, string? ifMatch, CancellationToken ct = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Versioned(path));
        if (json is not null)
            message.Content = Json(json);
        if (ifMatch is not null)
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return _http.SendAsync(message, ct);
    }

    public Task<HttpResponseMessage> PostAsync(string path, string? json = null, CancellationToken ct = default) =>
        _http.PostAsync(Versioned(path), json is null ? null : Json(json), ct);

    public static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct = default) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));

    public async Task SeedAsync(string subscription = "local", string resourceGroup = "dev", CancellationToken ct = default)
    {
        using var sub = await PutAsync($"/subscriptions/{subscription}", """{"properties":{"displayName":"local","state":"Enabled"}}""", ct);
        sub.EnsureSuccessStatusCode();
        using var rg = await PutAsync($"/subscriptions/{subscription}/resourceGroups/{resourceGroup}", """{"properties":{}}""", ct);
        rg.EnsureSuccessStatusCode();
    }

    private static StringContent Json(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();
}
