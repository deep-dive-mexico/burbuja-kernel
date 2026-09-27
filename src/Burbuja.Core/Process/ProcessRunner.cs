using System.Collections;
using System.Diagnostics;
using System.Text;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;

namespace Burbuja.Core.Process;

public sealed class ProcessRunner(IClock clock, IRedactor redactor) : IProcessRunner
{
    public async ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.ExpiresAt <= clock.UtcNow)
            throw new InvalidOperationException("ExecutionGrant has expired.");

        var parent = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string && e.Value is string)
            .ToDictionary(e => (string)e.Key!, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
        var env = ProcessEnvironmentPolicy.Build(parent, request.Environment);

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Clear();
        foreach (var (k, v) in env)
            psi.Environment[k] = v;
        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        var stdoutTask = ReadCappedAsync(p.StandardOutput, request.MaxOutputBytes, ct);
        var stderrTask = ReadCappedAsync(p.StandardError, request.MaxOutputBytes, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout > TimeSpan.Zero)
            timeout.CancelAfter(request.Timeout);
        var timedOut = false;
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await p.WaitForExitAsync(CancellationToken.None); } catch { /* ignore */ }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (request.RedactOutput)
        {
            stdout = redactor.Redact(stdout);
            stderr = redactor.Redact(stderr);
        }

        return new ProcessResult(timedOut ? -1 : p.ExitCode, stdout, stderr, timedOut);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        if (maxBytes <= 0)
            return "";
        var sb = new StringBuilder();
        var buf = new char[4096];
        var bytes = 0;
        while (bytes < maxBytes)
        {
            var remainingChars = Math.Min(buf.Length, Math.Max(1, maxBytes - bytes));
            var n = await reader.ReadAsync(buf.AsMemory(0, remainingChars), ct);
            if (n == 0)
                break;
            sb.Append(buf.AsSpan(0, n));
            bytes += Encoding.UTF8.GetByteCount(buf.AsSpan(0, n));
        }

        return sb.ToString();
    }
}
