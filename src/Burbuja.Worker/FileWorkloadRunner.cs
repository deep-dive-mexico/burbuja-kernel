using System.Diagnostics;
using System.Text.Json;
using Burbuja.Compute.Abstractions;

namespace Burbuja.Worker;

public sealed class WorkerOptions
{
    public string TokenFile { get; set; } = "";
    public string HeadroomFile { get; set; } = "";
    public string ScratchDirectory { get; set; } = "";
    public Dictionary<string, FileWorkload> Workloads { get; set; } = new();
    public void Validate()
    {
        if (!File.Exists(TokenFile) || File.ReadAllText(TokenFile).Trim().Length < 32 ||
            !Path.IsPathFullyQualified(HeadroomFile) || !Path.IsPathFullyQualified(ScratchDirectory) ||
            Workloads.Count == 0)
            throw new InvalidOperationException("Worker requires a token file, absolute headroom/scratch paths and workloads.");
        foreach (var definition in Workloads.Values) definition.Validate();
    }
}

/// <summary>Trusted operator configuration, never a caller-supplied executable or shell command.</summary>
public sealed class FileWorkload
{
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> ParameterFlags { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 10800;
    public long MaxInputBytes { get; set; } = 256 * 1024 * 1024;
    public int MaxOutputBytes { get; set; } = 4 * 1024 * 1024;
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Executable) || !File.Exists(Executable) ||
            TimeoutSeconds is < 1 or > 10800 || MaxInputBytes is < 1 or > 268435456 ||
            MaxOutputBytes is < 1 or > 16777216 || ParameterFlags.Values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Invalid fixed file workload.");
    }
    public string[] Bind(string input, string output, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Any(p => !ParameterFlags.ContainsKey(p.Key) || p.Value.Length > 8192 || p.Value.Contains('\0')))
            throw new ArgumentException("Unsupported workload parameter.");
        return Arguments.Select(a => a.Replace("{input}", input).Replace("{output}", output))
            .Concat(parameters.SelectMany(p => new[] { ParameterFlags[p.Key], p.Value })).ToArray();
    }
}

public sealed class WorkloadBusyException : Exception;

public sealed class FileWorkloadRunner(WorkerOptions options)
{
    private readonly SemaphoreSlim _slot = new(1);
    private string? _active;
    private long _completed;
    private long _failed;
    private DateTimeOffset? _started;
    public object Status => new { active = _active, started = _started, completed = Interlocked.Read(ref _completed),
        failed = Interlocked.Read(ref _failed), headroom = ReadHeadroom() };

    private WorkloadHeadroom? ReadHeadroom()
    {
        try { return JsonSerializer.Deserialize<WorkloadHeadroom>(File.ReadAllText(options.HeadroomFile), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private bool Admit() => WorkloadAdmission.CanStart(ReadHeadroom(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));

    public async Task<byte[]> RunAsync(string name, Stream input, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!options.Workloads.TryGetValue(name, out var definition)) throw new KeyNotFoundException();
        // Validate before allocating a slot or touching caller-controlled bytes.
        definition.Bind("input", "output", parameters);
        if (!Admit() || !await _slot.WaitAsync(0, ct)) throw new WorkloadBusyException();
        var directory = Path.Combine(options.ScratchDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            _active = name;
            _started = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var inputPath = Path.Combine(directory, "input");
            var outputBase = Path.Combine(directory, "output");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
            await using (var destination = File.Create(inputPath))
            {
                var buffer = new byte[65536];
                long size = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    size += count;
                    if (size > definition.MaxInputBytes) throw new InvalidDataException("Input too large.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                }
                if (size == 0) throw new InvalidDataException("Empty input.");
            }
            if (!Admit()) throw new WorkloadBusyException();
            var start = new ProcessStartInfo(definition.Executable) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
            start.Environment["HOME"] = directory;
            start.Environment["TMPDIR"] = directory;
            foreach (var argument in definition.Bind(inputPath, outputBase, parameters)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Worker could not start.");
            // Drain bounded chunks without retaining potentially private/unbounded diagnostic output.
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                while (!process.HasExited)
                {
                    var exit = process.WaitForExitAsync(deadline.Token);
                    if (await Task.WhenAny(exit, Task.Delay(5000, deadline.Token)) == exit) { await exit; break; }
                    deadline.Token.ThrowIfCancellationRequested();
                    var health = ReadHeadroom();
                    if (health is null || DateTimeOffset.UtcNow - health.ObservedAt > TimeSpan.FromSeconds(90) ||
                        health.Reason is "critical memory pressure" or "disk reserve" or "thermal reserve" or "thermal or power throttling"
                            or "protected service unhealthy" or "container unhealthy")
                        throw new IOException("Host protection interrupted the workload; caller retains input.");
                }
                await Task.WhenAll(stdout, stderr);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            }
            if (process.ExitCode != 0) throw new IOException("Workload process failed.");
            var output = outputBase + ".json";
            if (new FileInfo(output).Length > definition.MaxOutputBytes) throw new InvalidDataException("Output too large.");
            var bytes = await File.ReadAllBytesAsync(output, deadline.Token);
            using var parsed = JsonDocument.Parse(bytes);
            Interlocked.Increment(ref _completed);
            return bytes;
        }
        catch { Interlocked.Increment(ref _failed); throw; }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { _active = null; _started = null; _slot.Release(); }
        }
    }
}
