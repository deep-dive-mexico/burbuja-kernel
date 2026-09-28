using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Burbuja.TestKit;

/// <summary>
/// Locates the test repository and, only when requested, the kernel checkout. Consumer code may pass
/// an explicit root or discover any solution name without needing a kernel checkout beside it.
/// </summary>
public static class RepoLayout
{
    public static string Root => FindRoot(root: Environment.GetEnvironmentVariable("BURBUJA_TEST_ROOT"));

    public static string KernelRoot => FindKernelRoot();

    public static string Solution => FindSolutionFile(KernelRoot)
        ?? throw new InvalidOperationException($"no solution found in {KernelRoot}");

    public static string PluginsRoot => Path.Combine(KernelRoot, "plugins");

    public static string ScenariosRoot => Path.Combine(KernelRoot, "tests", "scenarios");

    /// <summary>
    /// An explicit root wins and needs no solution file. Otherwise walk upward from the supplied directory
    /// (default: the test assembly directory), finding the named solution or any .slnx/.sln.
    /// This method does not read environment variables; <see cref="Root"/> honors BURBUJA_TEST_ROOT.
    /// </summary>
    public static string FindRoot(string? startDirectory = null, string? solutionFileName = null, string? root = null)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            var fullPath = Path.GetFullPath(root);
            return Directory.Exists(fullPath) ? fullPath
                : throw new DirectoryNotFoundException($"test repository root does not exist: {fullPath}");
        }

        var start = startDirectory ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (solutionFileName is null ? FindSolutionFile(dir.FullName) is not null
                : File.Exists(Path.Combine(dir.FullName, solutionFileName)))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"no {solutionFileName ?? ".slnx or .sln"} found above {start}; pass a root or set BURBUJA_TEST_ROOT");
    }

    private static string FindKernelRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BURBUJA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return FindRoot(root: configured);

        var direct = FindSolution("Burbuja.slnx");
        if (direct is not null)
            return direct;

        // A consumer checkout: the kernel is normally the sibling `burbuja-server`.
        foreach (var candidate in new[] { "../burbuja-server", "../burbuja-core" })
        {
            var path = Path.GetFullPath(Path.Combine(Root, candidate));
            if (File.Exists(Path.Combine(path, "Burbuja.slnx")))
                return path;
        }

        throw new InvalidOperationException(
            $"Burbuja.slnx not found above {AppContext.BaseDirectory} or beside {Root}; set BURBUJA_ROOT");
    }

    private static string? FindSolutionFile(string root)
    {
        var historical = Path.Combine(root, "Burbuja.slnx");
        if (File.Exists(historical))
            return historical;
        return Directory.EnumerateFiles(root, "*.slnx").Concat(Directory.EnumerateFiles(root, "*.sln"))
            .Order(StringComparer.Ordinal).FirstOrDefault();
    }

    private static string? FindSolution(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, fileName)))
            dir = dir.Parent;
        return dir?.FullName;
    }
}

public static class FreePort
{
    /// <summary>
    /// M6.0: ports handed out in this process, so two callers cannot be told the same one.
    ///
    /// <para><see cref="Pick"/> only ever *tested* bindability and then let go, which was safe while
    /// every caller sat in one serialised collection. The moment two collections could overlap (the
    /// Disruptive split), both picked the same port and one Host died with "address already in use",
    /// reported as a bare exit 134 that reads like a product bug. A pick is now a claim, and the
    /// caller must <see cref="Release"/> it once its own Host owns the port (or has failed).</para>
    /// </summary>
    private static readonly HashSet<int> Claimed = [];
    private static readonly object Gate = new();

    /// <summary>
    /// The band this checkout may bind, from the environment a LANE sets (M14 R5p).
    ///
    /// <para><b>Why an environment band and not a wider range.</b> Four agents run the E2E suite on one
    /// Mac at the same time. <c>Pick</c> only claims a port within ONE process, so two lanes each picking
    /// from 18100-18399 can be handed the same number and the second Host dies with "address already in
    /// use" — reported as a bare exit 134 that reads like a product bug. The bands do not overlap
    /// (`main` 18100-18174 · kernel 18175-18249 · channels 18250-18324 · sources 18325-18398), so two
    /// lanes cannot collide even when their picks happen in the same millisecond.</para>
    ///
    /// <para>Read HERE rather than at every call site, so a fact that picks a port gets the band without
    /// knowing lanes exist. An unset or unparseable variable leaves the historical range, which is what
    /// keeps `main` behaving exactly as it did before the mechanism existed.</para>
    /// </summary>
    public static (int Min, int Max) Band { get; } = ReadBand();

    private static (int Min, int Max) ReadBand()
    {
        var min = ReadPort("BURBUJA_PORT_MIN", 18100);
        var max = ReadPort("BURBUJA_PORT_MAX", 18399);
        return min <= max ? (min, max) : (18100, 18399);
    }

    private static int ReadPort(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value is > 0 and < 65536
            ? value
            : fallback;
    }

    /// <summary>
    /// Finds a bindable loopback port in this checkout's band and claims it. The defaults are
    /// <see cref="Band"/>, so a caller that passes nothing gets the lane's range; a caller that passes an
    /// explicit range keeps it (a fact testing one specific port is not asking about lanes).
    /// </summary>
    public static int Pick(int? min = null, int? max = null)
    {
        var lo = min ?? Band.Min;
        var hi = max ?? Band.Max;
        lock (Gate)
        {
            for (var port = lo; port <= hi; port++)
            {
                if (Claimed.Contains(port))
                    continue;
                if (TryBind(port))
                {
                    Claimed.Add(port);
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"no free port in {lo}-{hi} (this checkout's band; BURBUJA_PORT_MIN/MAX set it)");
        }
    }

    /// <summary>Gives a claimed port back. Safe to call for a port this process never claimed.</summary>
    public static void Release(int port)
    {
        lock (Gate)
        {
            Claimed.Remove(port);
        }
    }

    private static bool TryBind(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>Starts an external process and captures its output to a log file.</summary>
public static class ProcessRunner
{
    public static Process Start(string fileName, IEnumerable<string> args, string workingDirectory, IReadOnlyDictionary<string, string?> environment, string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
        {
            if (value is not null)
                info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {fileName}");
        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine("[stderr] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory, IDictionary<string, string?>? environment, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is not null)
                    info.Environment[key] = value;
            }
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        // HostPublisher is intentionally synchronous and may run inside a fixture's
        // single-threaded context. Process completion must never need that blocked thread.
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode,
            await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
