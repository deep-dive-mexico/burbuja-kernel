using System.IO.Compression;
using System.Security.Cryptography;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Process;

namespace Burbuja.Compute.Backends;

internal static class BackendSupport
{
    public static bool IsInside(string root, string candidate)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(prefix, StringComparison.Ordinal) || string.Equals(full, Path.GetFullPath(root), StringComparison.Ordinal);
    }

    public static string Sanitize(string key)
    {
        // Use the last path segment (the workspace name), not the whole ARM id.
        var last = key.Trim().TrimEnd('/').Split('/').LastOrDefault() ?? key;
        var chars = last.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "workspace" : s;
    }

    public static async ValueTask<ComputeSnapshotResult> ZipAsync(
        string root,
        string name,
        IArtifactStore artifacts,
        CancellationToken ct)
    {
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

    public static async ValueTask<ComputeExecResult> RunBashAsync(
        IProcessRunner processes,
        IRedactor redactor,
        ComputeExecRequest request,
        ExecutionGrant grant,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(request.RootPath);
        var cwd = Path.GetFullPath(Path.Combine(root, request.Cwd.Length == 0 ? "." : request.Cwd));
        if (!IsInside(root, cwd))
            return new ComputeExecResult(-1, "", "CwdEscape", false, 0);

        var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Environment)
        {
            if (request.EnvAllowlist.Count > 0 && !request.EnvAllowlist.Contains(key, StringComparer.OrdinalIgnoreCase))
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
        return new ComputeExecResult(
            result.ExitCode,
            redactor.Redact(result.StandardOutput),
            redactor.Redact(result.StandardError),
            result.TimedOut,
            (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }
}
