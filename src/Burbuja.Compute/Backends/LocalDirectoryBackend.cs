using System.IO.Compression;
using System.Security.Cryptography;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Process;
using Microsoft.Extensions.Options;

namespace Burbuja.Compute.Backends;

/// <summary>
/// A workspace that is a directory under <c>Compute:LocalDirectory:Root</c>. All paths are
/// confined to that root; exec never inherits the parent environment beyond the allowlist.
/// </summary>
public sealed class LocalDirectoryBackend(
    IOptions<ComputeOptions> options,
    IProcessRunner processes,
    IArtifactStore artifacts,
    IRedactor redactor) : IComputeBackend
{
    public string Kind => "local-directory";

    public IReadOnlyList<string> SupportedImages { get; } = ["ubuntu-24.04", "alpine-3.20"];

    public IReadOnlyList<string> SupportedSizes { get; } = ["small", "medium"];

    public string ResolveRootPath(string workspaceKey) => ResolveRoot(workspaceKey, "");

    public ValueTask<ComputeProvisionResult> ProvisionAsync(ComputeProvisionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveRoot(request.WorkspaceKey, request.RootPath);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "work"));
        return new(new ComputeProvisionResult($"local:{root}", root, null, null));
    }

    public async ValueTask<ComputeExecResult> ExecAsync(ComputeExecRequest request, ExecutionGrant grant, CancellationToken ct)
    {
        var root = Path.GetFullPath(ResolveRoot("", request.RootPath));
        var cwd = Path.GetFullPath(Path.Combine(root, request.Cwd.Length == 0 ? "." : request.Cwd));
        if (!IsInside(root, cwd))
            return new ComputeExecResult(-1, "", "CwdEscape", false, 0);

        var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Environment)
        {
            if (request.EnvAllowlist.Count > 0
                && !request.EnvAllowlist.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            requested[key] = value;
        }

        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ProcessEnvironmentPolicy.Allowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
                parent[key] = value;
        }

        var env = ProcessEnvironmentPolicy.Build(parent, requested);
        var started = DateTimeOffset.UtcNow;
        var result = await processes.RunAsync(
            new ProcessStartRequest("/bin/bash", ["-lc", request.Command], cwd, env, request.Timeout, request.MaxOutputBytes, true),
            grant,
            ct);
        var duration = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        return new ComputeExecResult(
            result.ExitCode,
            redactor.Redact(result.StandardOutput),
            redactor.Redact(result.StandardError),
            result.TimedOut,
            duration);
    }

    public async ValueTask<ComputeSnapshotResult> SnapshotAsync(string rootPath, string name, CancellationToken ct)
    {
        var root = Path.GetFullPath(rootPath);
        var zipPath = Path.Combine(Path.GetTempPath(), $"burbuja-snapshot-{Guid.NewGuid():N}.zip");
        try
        {
            ZipFile.CreateFromDirectory(root, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            var bytes = await File.ReadAllBytesAsync(zipPath, ct);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var key = $"compute/snapshots/{Path.GetFileName(root)}/{name}.zip";
            await artifacts.PutAsync(key, new MemoryStream(bytes, writable: false), "application/zip", null, ct);
            return new ComputeSnapshotResult(name, key, bytes.LongLength, sha);
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (IOException)
            {
            }
        }
    }

    public ValueTask DestroyAsync(string rootPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveRoot("", rootPath);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return default;
    }

    public ValueTask<ComputeProbe> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = RootRoot();
        Directory.CreateDirectory(root);
        return new(new ComputeProbe(Directory.Exists(root), Kind));
    }

    private string ResolveRoot(string workspaceKey, string candidate)
    {
        var baseRoot = RootRoot();
        var name = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (string.IsNullOrEmpty(name))
            name = Sanitize(workspaceKey);
        var combined = Path.GetFullPath(Path.Combine(baseRoot, name));
        if (!IsInside(baseRoot, combined))
            throw new InvalidOperationException($"compute workspace path escapes {baseRoot}");
        return combined;
    }

    private string RootRoot()
    {
        var configured = options.Value.LocalDirectory.Root;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Path.Combine(Path.GetTempPath(), "burbuja-compute");
        return Path.GetFullPath(configured);
    }

    private static bool IsInside(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal)
               || string.Equals(candidate, root, StringComparison.Ordinal);
    }

    private static string Sanitize(string key)
    {
        var last = key.Trim().TrimEnd('/').Split('/').LastOrDefault() ?? key;
        var chars = last.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "workspace" : s;
    }
}
