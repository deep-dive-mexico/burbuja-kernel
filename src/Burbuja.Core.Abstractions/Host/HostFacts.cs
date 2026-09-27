namespace Burbuja.Core.Abstractions.Host;

/// <summary>
/// What the host says about the battery. Every member is nullable on purpose and
/// <c>null</c> always means <em>unknown</em>, never zero: a desktop Mac has no battery at all
/// (<c>Battery(null, null, null)</c>), and a machine whose reading failed must say "unknown" rather
/// than "0%" or "not charging".
/// </summary>
/// <param name="Percent">Charge percentage, 0-100.</param>
/// <param name="Charging">True while the battery is taking charge. False when a battery is present
/// and is not charging (on battery, or plugged in and full) — <c>null</c> only when there is no
/// battery reading at all.</param>
/// <param name="EstimateMinutes">Time to empty in <b>minutes</b>, from the best source that
/// answered: the gas gauge's own averaged value (<c>ioreg</c> <c>AvgTimeToEmpty</c>) when macOS
/// reports one, otherwise the <c>h:mm</c> that <c>pmset -g batt</c> prints, converted. One member
/// and one unit because both sources are the same number at different rounding — and because a
/// string reader has to re-guess the unit, which is how <c>"20:00 remaining"</c> gets read as twenty
/// minutes. Null when neither source had an estimate; never 0, which is what pmset prints for a
/// charged battery on AC and is the absence of an estimate rather than a very short one.</param>
public sealed record BatteryFacts(decimal? Percent, bool? Charging, int? EstimateMinutes);

/// <summary>
/// One reading of the host this process runs on. Produced by an <see cref="IHostFactsProvider"/> and
/// held by an <see cref="IHostFactsSnapshot"/>. Every field is nullable for one reason: the kernel
/// must never turn "I could not read it" into a number. A zero is a fact (an empty disk, no swap in
/// use); <c>null</c> is the absence of one, and the two are never interchangeable.
/// </summary>
/// <param name="Battery">Battery facts; all-null members when the machine has no battery.</param>
/// <param name="MemoryFreeMb">Free RAM in MB, with reclaimable (inactive + speculative) pages counted
/// as free, the way the reference host check does it.</param>
/// <param name="MemoryPressureLevel">0-100, higher is healthier (<c>kern.memorystatus_level</c>).</param>
/// <param name="SwapUsedMb">Swap in use, in MB.</param>
/// <param name="DiskFreeGb">Free space on the root volume, in GB.</param>
/// <param name="CpuSpeedLimit">Thermal CPU speed limit as macOS prints it (<c>"100"</c> = unclamped);
/// null when macOS reports nothing (Apple Silicon does not).</param>
/// <param name="UptimeSec">Seconds since boot, from <c>kern.boottime</c>.</param>
/// <param name="SleepDisabled">Whether system sleep is disabled (<c>pmset -g SleepDisabled</c>).</param>
/// <param name="TailscaleOnline">Whether the tailnet is up. Null when Tailscale is not installed or
/// cannot be asked — "unknown", never "offline".</param>
/// <param name="ProcessRssMb">Resident set size of this process, in MB.</param>
/// <param name="Extra">Platform-specific extras (version/chip/provenance strings). Values are
/// pre-formatted strings so a reader never has to guess a unit.</param>
/// <param name="At">When this reading was taken.</param>
public sealed record HostFacts(
    BatteryFacts Battery,
    decimal? MemoryFreeMb,
    decimal? MemoryPressureLevel,   // 0-100, higher is healthier (kern.memorystatus_level)
    decimal? SwapUsedMb,
    decimal? DiskFreeGb,
    string? CpuSpeedLimit,
    decimal? UptimeSec,
    bool? SleepDisabled,
    bool? TailscaleOnline,
    decimal? ProcessRssMb,
    IReadOnlyDictionary<string, string> Extra,
    DateTimeOffset At);

public interface IHostFactsProvider
{
    /// <summary>Stable id of the platform this provider speaks for, e.g. "macos".</summary>
    string Platform { get; }

    /// <summary>Collect facts now. Returns null when collection is impossible here.</summary>
    ValueTask<HostFacts?> CollectAsync(CancellationToken ct);
}

/// <summary>A process-wide, read-only view of the most recent collection.</summary>
public interface IHostFactsSnapshot
{
    HostFacts? Latest { get; }

    void Publish(HostFacts facts);
}
