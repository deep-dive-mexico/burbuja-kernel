namespace Burbuja.Agents.Model;

public sealed class TeamProperties
{
    public string DisplayName { get; set; } = "";
    public string? LeadAgentId { get; set; }
    public List<string> Members { get; set; } = [];
    public int BoardRevision { get; set; } = 1;
}

public static class TeamTaskStatus
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>The board's revision mirrors the resource ETag; a stale PATCH is a 412.</summary>
public sealed class TeamTaskProperties
{
    public string Title { get; set; } = "";
    public string Status { get; set; } = TeamTaskStatus.Pending;
    public int Revision { get; set; } = 1;
    public List<string> BlockedBy { get; set; } = [];
    public string? OwnerAgentId { get; set; }
    public List<string> WriteScopes { get; set; } = [];
    public string? Brief { get; set; }
    public bool Deleted { get; set; }
}

public sealed class TeamMessageProperties
{
    public string FromAgentId { get; set; } = "";
    public string? ToAgentId { get; set; }
    public string Body { get; set; } = "";
    public string? DedupKey { get; set; }
    public bool Delivered { get; set; }
}
