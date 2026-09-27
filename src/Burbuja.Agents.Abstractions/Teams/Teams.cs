namespace Burbuja.Agents.Abstractions.Teams;

/// <summary>The board's authority is the store; a snapshot is what a member reads.</summary>
public sealed record TeamTaskSnapshot(
    string Id,
    string Title,
    string Status,
    int Revision,
    IReadOnlyList<string> BlockedBy,
    string? OwnerAgentId,
    IReadOnlyList<string> WriteScopes,
    bool Deleted = false);

/// <summary>Bounds that fail loud rather than silently truncating.</summary>
public static class TeamBounds
{
    public const int RosterMax = 8;
    public const int BoardMax = 256;
    public const int PendingPerMemberMax = 64;
    public const int MessageMaxBytes = 64 * 1024;
}

public sealed record WaitForChangeResult(bool TimedOut, long AtSeq, IReadOnlyList<string> Changed);

public sealed record HandoffReport(
    string MemberId,
    string TaskId,
    string Summary,
    IReadOnlyList<string> Artifacts,
    string? BlockedReason);

public static class WaitForChangeBounds
{
    public static readonly TimeSpan Floor = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Ceiling = TimeSpan.FromHours(1);

    public static bool IsValid(TimeSpan timeout) => timeout >= Floor && timeout <= Ceiling;
}
