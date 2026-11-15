using System.Diagnostics;

namespace Burbuja.Cluster.Abstractions;

/// <summary>
/// Reads the output of a local tool for a facts source or a network provider.
///
/// <para><b>Standard error is never returned.</b> Tools print credentials and private names in their error
/// text (an overlay client can echo an auth key), so a failed probe yields null and the caller reports a fixed
/// label of its own. The deadline is short because a probe sits on the path of every node description; a tool
/// that hangs must cost one missing fact, not a stalled node.</para>
/// </summary>
public static class ProbeCommand
{
    public static async ValueTask<string?> ReadAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(fileName) || !File.Exists(fileName)) return null;
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;

        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                await stderr;
                return process.ExitCode == 0 ? output : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The first of <paramref name="candidates"/> that exists, for tools installed in different places.</summary>
    public static string? Find(params string[] candidates) => candidates.FirstOrDefault(File.Exists);
}

/// <summary>Thrown by a driver when this node's own configuration does not allow the work it was offered.</summary>
public sealed class WorkloadRefusedException(string reason) : Exception(reason);
