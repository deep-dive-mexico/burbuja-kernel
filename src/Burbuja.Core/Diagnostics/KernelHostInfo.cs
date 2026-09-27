using System.Reflection;
using System.Runtime.InteropServices;
using Burbuja.Core.Options;

namespace Burbuja.Core.Diagnostics;

/// <summary>
/// Track 3a: the identity of the process a health probe is talking to. Without it a
/// <c>/health/plugins</c> answer from the Pi and from this Mac are indistinguishable in a log or an
/// alert, and "which build is actually running?" costs an ssh. <see cref="WorkingSetBytes"/> is
/// read live (unlike the rest, which is the boot-time snapshot), so the number is not frozen at
/// startup.
/// </summary>
public sealed record KernelHostInfo(
    string NodeId,
    string Role,
    int Pid,
    DateTimeOffset StartedAt,
    string MachineName,
    string OS,
    string Arch,
    string Framework,
    string Version,
    string? PluginsDirectory)
{
    /// <summary>When this process first touched the type — as close to process start as the kernel needs.</summary>
    private static readonly DateTimeOffset ProcessStartedAt = DateTimeOffset.UtcNow;

    public long WorkingSetBytes => Environment.WorkingSet;

    public static KernelHostInfo Capture(KernelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var assembly = typeof(KernelHostInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        return new KernelHostInfo(
            options.NodeId,
            options.Role,
            Environment.ProcessId,
            ProcessStartedAt,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            version,
            string.IsNullOrWhiteSpace(options.PluginsDirectory) ? null : options.PluginsDirectory);
    }
}
