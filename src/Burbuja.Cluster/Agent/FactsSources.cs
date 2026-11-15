using System.Globalization;
using System.Runtime.InteropServices;
using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster.Agent;

/// <summary>
/// Processors, memory and platform, read the way each system publishes them. A value that cannot be read is
/// left out: the node then reports no reading at all, which the cluster treats as "cannot see" rather than
/// as an idle machine.
/// </summary>
internal sealed class SystemFactsSource(ClusterOptions options) : INodeFactsSource
{
    private const decimal Mib = 1024m * 1024m;

    public string Id => "system";

    public ValueTask DescribeAsync(NodeDescription description, CancellationToken ct)
    {
        var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unknown";
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        description.Platform = new NodePlatform(os, architecture, Environment.OSVersion.VersionString);
        description.Capacity[ClusterConstants.Resources.Cpu] = Environment.ProcessorCount;
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (memory > 0) description.Capacity[ClusterConstants.Resources.MemoryMb] = Math.Floor(memory / Mib);

        // Every Apple Silicon Mac has one accelerator sharing main memory, reached through Metal.
        if (os == "macos" && architecture == "arm64")
        {
            description.Capabilities.Add(ClusterConstants.Capabilities.Mps);
            description.Devices.Add(new NodeDevice(ClusterConstants.Resources.Gpu, "0", "Apple GPU", ClusterConstants.Capabilities.Mps));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask ObserveAsync(NodeReading reading, CancellationToken ct)
    {
        var (load, freeMb) = OperatingSystem.IsLinux() ? ReadLinux() : OperatingSystem.IsMacOS() ? await ReadMacAsync(ct) : (null, null);
        var cores = Environment.ProcessorCount;
        if (load is { } busy)
        {
            reading.Available[ClusterConstants.Resources.Cpu] = Math.Max(0, cores - busy);
            reading.Report(busy / cores * 100 >= options.Agent.CpuBusyPercent ? PressureLevel.Elevated : PressureLevel.Normal, "processors are busy");
        }

        if (freeMb is { } free)
        {
            reading.Available[ClusterConstants.Resources.MemoryMb] = free;
            reading.Report(free < options.Agent.MinFreeMemoryMb ? PressureLevel.Critical : PressureLevel.Normal, "memory is low");
        }
    }

    private static (decimal? Load, decimal? FreeMb) ReadLinux()
    {
        decimal? load = null;
        decimal? free = null;
        try
        {
            if (decimal.TryParse(File.ReadAllText("/proc/loadavg").Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one)) load = one;
            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
            if (line is not null && decimal.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kb)) free = Math.Floor(kb / 1024m);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
        {
            // Unreadable is unknown.
        }

        return (load, free);
    }

    private static async ValueTask<(decimal? Load, decimal? FreeMb)> ReadMacAsync(CancellationToken ct)
    {
        var output = await ProbeCommand.ReadAsync("/usr/sbin/sysctl", ["-n", "vm.loadavg", "kern.memorystatus_level"], TimeSpan.FromSeconds(3), ct);
        return output is null ? (null, null) : ParseMac(output, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    /// <summary><c>vm.loadavg</c> prints <c>{ 1.52 1.60 1.71 }</c>; <c>kern.memorystatus_level</c> is the percentage of memory still available.</summary>
    internal static (decimal? Load, decimal? FreeMb) ParseMac(string sysctl, long totalBytes)
    {
        decimal? load = null;
        decimal? free = null;
        var lines = sysctl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 0)
        {
            var first = lines[0].Trim('{', '}', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (decimal.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var one)) load = one;
        }

        if (lines.Length > 1 && decimal.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var level) && totalBytes > 0)
            free = Math.Floor(totalBytes / Mib * level / 100m);
        return (load, free);
    }
}

/// <summary>
/// NVIDIA accelerators, through a read-only <c>nvidia-smi</c> query. It lists devices and nothing else: it
/// never changes a mode, a binding or a driver, because a node's accelerators are often shared with people
/// and services the cluster knows nothing about.
/// </summary>
internal sealed class NvidiaFactsSource : INodeFactsSource
{
    public string Id => "nvidia";

    public async ValueTask DescribeAsync(NodeDescription description, CancellationToken ct)
    {
        var tool = ProbeCommand.Find("/usr/bin/nvidia-smi", "/usr/local/bin/nvidia-smi");
        if (tool is null) return;
        var output = await ProbeCommand.ReadAsync(
            tool, ["--query-gpu=index,name,memory.total", "--format=csv,noheader,nounits"], TimeSpan.FromSeconds(5), ct);
        var devices = output is null ? [] : Parse(output);
        if (devices.Count == 0) return;
        description.Capabilities.Add(ClusterConstants.Capabilities.Cuda);
        description.Devices.AddRange(devices);
    }

    public ValueTask ObserveAsync(NodeReading reading, CancellationToken ct) => ValueTask.CompletedTask;

    internal static List<NodeDevice> Parse(string csv)
    {
        var devices = new List<NodeDevice>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || !int.TryParse(fields[0], out var index)) continue;
            decimal? memory = decimal.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mib) ? mib : null;
            devices.Add(new NodeDevice(ClusterConstants.Resources.Gpu, index.ToString(CultureInfo.InvariantCulture), fields[1], ClusterConstants.Capabilities.Cuda, memory));
        }

        return devices;
    }
}
