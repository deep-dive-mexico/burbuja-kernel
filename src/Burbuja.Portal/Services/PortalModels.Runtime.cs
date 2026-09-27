namespace Burbuja.Portal.Services;

/// <summary>Telemetry about an ARM request, distinct from the subject's decision audit.</summary>
public sealed class PortalActivityRow
{
    public DateTimeOffset At { get; set; }
    public double DurationMs { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ResourceId { get; set; }
    public string? PrincipalId { get; set; }
    public string? PrincipalType { get; set; }
    public string? ApiVersion { get; set; }
    public int Status { get; set; }
    public string? ErrorCode { get; set; }
    public string? RequestId { get; set; }
    public string? CorrelationId { get; set; }
    public bool IsDataPlane { get; set; }
}

/// <summary>The request ring's own counts. Zero capacity means the host did not report its ring.</summary>
public sealed class PortalActivityRing
{
    public long Total { get; set; }
    public int Capacity { get; set; }
    public int Held { get; set; }
    public string Describe() => Capacity == 0 ? "the host did not report its ring"
        : $"{Held}/{Capacity} held · {Total} served since this process started";
}

/// <summary>The kernel's workspace projection; the UI does not recalculate these totals.</summary>
public sealed class PortalWorkspaceHub
{
    public int Jobs { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new(StringComparer.Ordinal);
    public List<PortalWorkspaceJob> Recent { get; set; } = [];
    public decimal SpendUsd { get; set; }
    public decimal? DefaultMaxUsd { get; set; }
    public int Schedules { get; set; }
    public int PausedSchedules { get; set; }
    public DateTimeOffset? NextScheduleRun { get; set; }
    public int Scanned { get; set; }
    public bool Truncated { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public static readonly IReadOnlyList<string> GroupOrder =
        ["active", "parked", "blocked", "queued", "paused", "succeeded", "failed", "cancelled"];
    public IEnumerable<(string Group, int Count)> Groups() =>
        GroupOrder.Where(ByStatus.ContainsKey).Select(g => (g, ByStatus[g]));
    public bool IsPartial => Truncated;
}

public sealed class PortalWorkspaceJob
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal Usd { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? PendingApprovalId { get; set; }
}
