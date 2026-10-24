namespace Burbuja.Compute.Abstractions;

/// <summary>Operator-observed headroom. Missing/stale telemetry never admits new batch work.</summary>
public sealed record WorkloadHeadroom(DateTimeOffset ObservedAt, bool Admit, string Reason);

public static class WorkloadAdmission
{
    public static bool CanStart(WorkloadHeadroom? sample, DateTimeOffset now, TimeSpan maximumAge) =>
        sample is { Admit: true } && maximumAge > TimeSpan.Zero &&
        sample.ObservedAt <= now + TimeSpan.FromSeconds(5) && now - sample.ObservedAt <= maximumAge;
}
