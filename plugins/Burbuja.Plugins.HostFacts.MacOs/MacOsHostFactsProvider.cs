using System.Globalization;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Plugins.HostFactsMacOs.Parsers;

namespace Burbuja.Plugins.HostFactsMacOs;

/// <summary>
/// Collects macOS host facts by running a fixed list of read-only commands through the kernel's
/// <see cref="IProcessRunner"/> — the same gated path every other process-spawning plugin uses, with
/// a minted <see cref="ExecutionGrant"/>, never <c>System.Diagnostics.Process</c> directly.
///
/// <para><b>Total by construction.</b> Every command gets its own 5 second timeout and 64 KB output
/// cap (the request enforces both), and every failure mode — a missing binary, a non-zero exit, a
/// timeout, a thrown exception, an output nobody can parse — leaves that one field null. Nothing here
/// throws except a caller-driven cancellation, and nothing here invents a zero: an unreadable disk is
/// null, an empty disk is 0.</para>
///
/// <para><b>This provider never returns null.</b> The abstraction's null means "collection is
/// impossible here", but a host where every single probe failed is still a host this provider can
/// speak for — it speaks by returning facts with every field null, which is exactly the honest answer
/// and the one the caller can render. Null is reserved for a provider that cannot speak for the
/// platform at all.</para>
/// </summary>
public sealed class MacOsHostFactsProvider(
    IProcessRunner processes,
    IExecutionGrantAuthority grants,
    IClock clock) : IHostFactsProvider
{
    public const string MacOsPlatform = "macos";

    private const string Pmset = "/usr/bin/pmset";
    private const string Ioreg = "/usr/sbin/ioreg";
    private const string VmStat = "/usr/bin/vm_stat";
    private const string Sysctl = "/usr/sbin/sysctl";
    private const string Df = "/bin/df";
    private const string Ps = "/bin/ps";

    /// <summary>The app-bundle CLI is where Tailscale ships on macOS; the bare name is the fallback
    /// for a Homebrew or hand-installed client, resolved through the gated PATH.</summary>
    private const string TailscaleApp = "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
    private const string TailscalePath = "tailscale";
    private const string TailscaleAbsent = "absent";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private const int MaxOutputBytes = 64 * 1024;

    /// <summary>
    /// Every absolute binary this provider shells out to, in one place that a fact can check.
    ///
    /// <para><b>Why it is public.</b> A stub runner answers any path, so a probe whose path is wrong
    /// passes every fact in the suite and silently returns nothing on the real machine — which is
    /// exactly what `/usr/bin/ioreg` did: `ioreg` lives in <c>/usr/sbin</c>, the provider fell back
    /// to pmset's rounded estimate, and only the LIVE host showed it
    /// (<c>Extra["batteryEstimateSource"] = "pmset"</c> where it should have said <c>gauge</c>). The
    /// fact that consumes this asserts each path exists on a macOS host.</para>
    /// </summary>
    public static IReadOnlyList<string> ProbePaths { get; } = [Pmset, Ioreg, VmStat, Sysctl, Df, Ps];

    public string Platform => MacOsPlatform;

    public async ValueTask<HostFacts?> CollectAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var grant = MintGrant();

        string? pmsetBatt = null, pmsetTherm = null, vmStatOut = null, pressureOut = null;
        string? swapOut = null, dfOut = null, bootOut = null, pmsetOut = null, psOut = null;
        string? ioregOut = null;
        var tailscaleCli = TailscaleAbsent;
        string? tailscaleOut = null;

        if (grant is not null)
        {
            var batteryTask = ReadAsync(Pmset, ["-g", "batt"], grant, ct);
            var gaugeTask = ReadAsync(Ioreg, ["-rn", "AppleSmartBattery"], grant, ct);
            var thermTask = ReadAsync(Pmset, ["-g", "therm"], grant, ct);
            var vmStatTask = ReadAsync(VmStat, [], grant, ct);
            var pressureTask = ReadAsync(Sysctl, ["kern.memorystatus_level"], grant, ct);
            var swapTask = ReadAsync(Sysctl, ["vm.swapusage"], grant, ct);
            var diskTask = ReadAsync(Df, ["-k", "/"], grant, ct);
            var bootTask = ReadAsync(Sysctl, ["-n", "kern.boottime"], grant, ct);
            var sleepTask = ReadAsync(Pmset, ["-g"], grant, ct);
            var rssTask = ReadAsync(Ps, ["-o", "rss=", "-p", Pid], grant, ct);

            await Task.WhenAll(batteryTask, gaugeTask, thermTask, vmStatTask, pressureTask, swapTask, diskTask, bootTask, sleepTask, rssTask);

            pmsetBatt = await batteryTask;
            ioregOut = await gaugeTask;
            pmsetTherm = await thermTask;
            vmStatOut = await vmStatTask;
            pressureOut = await pressureTask;
            swapOut = await swapTask;
            dfOut = await diskTask;
            bootOut = await bootTask;
            pmsetOut = await sleepTask;
            psOut = await rssTask;

            (tailscaleCli, tailscaleOut) = await ReadTailscaleAsync(grant, ct);
        }

        var disk = DiskFreeParser.Parse(dfOut);
        var gauge = IoregBatteryParser.ParseMinutes(ioregOut);
        var battery = GaugeFirst(BatteryParser.Parse(pmsetBatt), gauge);
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // BatteryFacts has no source member, but "on AC and full" and "on battery and full" are
            // different machines. Carried in Extra rather than invented as a field.
            ["batterySource"] = BatteryParser.Source(pmsetBatt),
            // Which of the two sources the estimate in `BatteryFacts.EstimateMinutes` came from —
            // "both are null" and "the gauge had nothing" are the same reading otherwise.
            ["batteryEstimateSource"] = battery.EstimateMinutes is null
                ? "unknown"
                : gauge is not null ? "gauge" : "pmset",
            ["tailscaleCli"] = tailscaleCli,
        };
        if (VmStatParser.ParsePageSizeBytes(vmStatOut) is { } pageSize)
            extra["memoryPageSizeBytes"] = pageSize.ToString(CultureInfo.InvariantCulture);
        if (disk is not null)
        {
            extra["diskTotalGb"] = disk.TotalGb.ToString(CultureInfo.InvariantCulture);
            extra["diskUsedGb"] = disk.UsedGb.ToString(CultureInfo.InvariantCulture);
        }

        return new HostFacts(
            battery,
            VmStatParser.ParseFreeMegabytes(vmStatOut),
            SysctlParser.ParseMemoryPressureLevel(pressureOut),
            SysctlParser.ParseSwapUsedMb(swapOut),
            disk?.FreeGb,
            PmsetParser.ParseCpuSpeedLimit(pmsetTherm),
            SysctlParser.ParseUptimeSeconds(bootOut, now),
            PmsetParser.ParseSleepDisabled(pmsetOut),
            TailscaleParser.Parse(tailscaleOut),
            PsParser.ParseRssMb(psOut),
            extra,
            now);
    }

    /// <summary>
    /// The gauge's minutes when it answered, pmset's own <c>h:mm</c> when it did not. pmset stays in
    /// the picture because ioreg is the one probe that can come back empty for a reason that has
    /// nothing to do with the battery (a machine that will not let the registry be read at all), and
    /// a rounded estimate beats no estimate.
    /// </summary>
    private static BatteryFacts GaugeFirst(BatteryFacts fromPmset, int? gaugeMinutes) =>
        gaugeMinutes is { } minutes ? fromPmset with { EstimateMinutes = minutes } : fromPmset;

    /// <summary>
    /// One grant per collection, for every command in it. A grant that cannot be minted (a broken
    /// authority, an expired clock) is not a reason to throw at a caller that asked for facts: the
    /// probe list is simply skipped and every field comes back null.
    /// </summary>
    private ExecutionGrant? MintGrant()
    {
        try
        {
            return grants.Mint("hostfacts:macos", "hostfacts.collect", TimeSpan.FromMinutes(1));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs one command and returns its stdout only when the command actually succeeded.</summary>
    private async Task<string?> ReadAsync(string fileName, string[] arguments, ExecutionGrant grant, CancellationToken ct)
    {
        var result = await RunAsync(fileName, arguments, grant, ct);
        return result is { TimedOut: false, ExitCode: 0 } ? result.StandardOutput : null;
    }

    /// <summary>
    /// Tailscale is the one command whose exit code is not a success signal: the CLI exits non-zero
    /// for "Logged out." just as it does for a real error, and the message on stdout is the only thing
    /// that tells the two apart. So stdout is kept whatever the exit code — except for a timeout,
    /// where a truncated message is not an answer — and it is the parser that decides unknown vs
    /// offline. A binary that cannot be started at all leaves both null and reports <c>absent</c>.
    /// </summary>
    private async Task<(string Cli, string? Output)> ReadTailscaleAsync(ExecutionGrant grant, CancellationToken ct)
    {
        var appBundle = File.Exists(TailscaleApp) ? TailscaleApp : null;
        var cli = appBundle ?? TailscalePath;
        var result = await RunAsync(cli, ["status"], grant, ct);
        var output = result is { TimedOut: false } ? result.StandardOutput : null;
        if (result is null && appBundle is null)
            return (TailscaleAbsent, null);
        return (cli, output);
    }

    /// <summary>Runs a command, turning every failure into null. Cancellation is the one exception:
    /// a cancelled caller does not want a reading, it wants to stop.</summary>
    private async Task<ProcessResult?> RunAsync(string fileName, string[] arguments, ExecutionGrant grant, CancellationToken ct)
    {
        try
        {
            return await processes.RunAsync(
                new ProcessStartRequest(fileName, arguments, "/", new Dictionary<string, string>(), CommandTimeout, MaxOutputBytes),
                grant,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Missing binary, a grant that expired between mint and use, a spawn the OS refused.
            return null;
        }
    }

    private static string Pid => Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
}
