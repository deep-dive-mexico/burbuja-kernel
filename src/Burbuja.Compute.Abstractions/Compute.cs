using System.Text.Json.Serialization;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Process;

namespace Burbuja.Compute.Abstractions;

public static class ComputeConstants
{
    public const string Namespace = "Burbuja.Compute";
    public const string ApiVersion = "2026-09-01";
    public const string TopicLifecycle = "compute.lifecycle";
    public const string StreamType = "compute.workspace";

    public static string StreamId(string canonicalId) => "compute.workspace/" + canonicalId.ToLowerInvariant();

    public static string LeaseId(string key) => "compute/" + key.ToLowerInvariant();
}

public enum ComputeWorkspaceState
{
    Creating,
    Running,
    Stopping,
    Stopped,
    Deallocating,
    Deallocated,
    Deleting,
    Deleted,
    Failed,
}

/// <summary>The pure transition table for a workspace's observed state.</summary>
public static class ComputeLifecycle
{
    private static readonly Dictionary<ComputeWorkspaceState, ComputeWorkspaceState[]> Allowed = new()
    {
        [ComputeWorkspaceState.Creating] = [ComputeWorkspaceState.Running, ComputeWorkspaceState.Failed, ComputeWorkspaceState.Deleting],
        [ComputeWorkspaceState.Running] = [ComputeWorkspaceState.Stopping, ComputeWorkspaceState.Deallocating, ComputeWorkspaceState.Deleting, ComputeWorkspaceState.Failed],
        [ComputeWorkspaceState.Stopping] = [ComputeWorkspaceState.Stopped, ComputeWorkspaceState.Failed],
        [ComputeWorkspaceState.Stopped] = [ComputeWorkspaceState.Running, ComputeWorkspaceState.Deallocating, ComputeWorkspaceState.Deleting],
        [ComputeWorkspaceState.Deallocating] = [ComputeWorkspaceState.Deallocated, ComputeWorkspaceState.Failed],
        [ComputeWorkspaceState.Deallocated] = [ComputeWorkspaceState.Creating, ComputeWorkspaceState.Deleting],
        [ComputeWorkspaceState.Deleting] = [ComputeWorkspaceState.Deleted, ComputeWorkspaceState.Failed],
        [ComputeWorkspaceState.Deleted] = [],
        [ComputeWorkspaceState.Failed] = [ComputeWorkspaceState.Creating, ComputeWorkspaceState.Deleting],
    };

    public static bool CanTransition(ComputeWorkspaceState from, ComputeWorkspaceState to) =>
        Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    public static bool IsTerminal(ComputeWorkspaceState state) => state is ComputeWorkspaceState.Deleted;

    public static bool IsActive(ComputeWorkspaceState state) =>
        state is ComputeWorkspaceState.Creating or ComputeWorkspaceState.Running
            or ComputeWorkspaceState.Stopping or ComputeWorkspaceState.Deallocating;
}

public sealed class ComputeRepoBinding
{
    public string? AgentsWorkspaceId { get; set; }
    public string? Remote { get; set; }
    public string? BaseRef { get; set; }
    public string? BranchPrefix { get; set; }
}

/// <summary>Named to avoid colliding with the Agents application's WorkspaceProperties.</summary>
public sealed class ComputeWorkspaceProperties
{
    public string DisplayName { get; set; } = "";
    public string Backend { get; set; } = "local-directory";
    public string Image { get; set; } = "ubuntu-24.04";
    public string Size { get; set; } = "small";
    public ComputeRepoBinding Repo { get; set; } = new();
    public List<string> EnvAllowlist { get; set; } = [];
    public TimeSpan? Ttl { get; set; }
    public TimeSpan? IdleTimeout { get; set; }

    // Observed state — never accepted from the client.
    public ComputeWorkspaceState State { get; set; } = ComputeWorkspaceState.Creating;
    public string? OperationId { get; set; }
    public string? SpecHash { get; set; }
    public int Generation { get; set; }
    public string? BackendHandle { get; set; }

    /// <summary>Host filesystem path; never serialized onto the wire.</summary>
    [JsonIgnore]
    public string? RootPath { get; set; }

    public string? Branch { get; set; }
    public string? BaseSha { get; set; }
    public string? LastSnapshotName { get; set; }
    public DateTimeOffset? ProvisionedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Error { get; set; }
}

public sealed class ComputeSnapshotProperties
{
    public string WorkspaceName { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class ComputeBackendProjection
{
    public string Kind { get; set; } = "";
    public List<string> SupportedImages { get; set; } = [];
    public List<string> SupportedSizes { get; set; } = [];
}

public sealed record ComputeProvisionRequest(string WorkspaceKey, string RootPath, ComputeWorkspaceProperties Spec);

public sealed record ComputeProvisionResult(string BackendHandle, string RootPath, string? Branch, string? BaseSha);

public sealed record ComputeExecRequest(
    string RootPath,
    string Command,
    string Cwd,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> EnvAllowlist,
    TimeSpan Timeout,
    int MaxOutputBytes);

public sealed record ComputeExecResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, int DurationMs);

public sealed record ComputeSnapshotResult(string Name, string ArtifactKey, long SizeBytes, string Sha256);

public sealed record ComputeProbe(bool Ready, string Detail);

public interface IComputeBackend
{
    string Kind { get; }
    IReadOnlyList<string> SupportedImages { get; }
    IReadOnlyList<string> SupportedSizes { get; }

    /// <summary>
    /// The host path a workspace key maps to. Pure: the workspace's RootPath is never persisted
    /// (it must not reach the wire), so callers re-derive it from the key.
    /// </summary>
    string ResolveRootPath(string workspaceKey);

    ValueTask<ComputeProvisionResult> ProvisionAsync(ComputeProvisionRequest request, CancellationToken ct);
    ValueTask<ComputeExecResult> ExecAsync(ComputeExecRequest request, ExecutionGrant grant, CancellationToken ct);
    ValueTask<ComputeSnapshotResult> SnapshotAsync(string rootPath, string name, CancellationToken ct);
    ValueTask DestroyAsync(string rootPath, CancellationToken ct);
    ValueTask<ComputeProbe> ProbeAsync(CancellationToken ct);
}

/// <summary>How other RPs (the Agents bridge) attach to and use a workspace.</summary>
public interface IComputeWorkspaceAccess
{
    ValueTask<ComputeProvisionResult> AttachAsync(string armId, string consumerId, CancellationToken ct);
    ValueTask<ComputeExecResult> ExecAsync(string armId, ComputeExecRequest request, CancellationToken ct);
    ValueTask TouchAsync(string armId, CancellationToken ct);
}

public static class ComputeEvents
{
    public const string WorkspaceAccepted = "compute.workspace.accepted";
    public const string WorkspaceRunning = "compute.workspace.running";
    public const string WorkspaceStopped = "compute.workspace.stopped";
    public const string WorkspaceDeallocated = "compute.workspace.deallocated";
    public const string WorkspaceDeleted = "compute.workspace.deleted";
    public const string WorkspaceFailed = "compute.workspace.failed";
    public const string WorkspaceReaped = "compute.workspace.reaped";
    public const string PolicyIntent = "compute.workspace.policy_intent";
    public const string PolicyDecision = "compute.workspace.policy_decision";
    public const string ExecStarted = "compute.exec.started";
    public const string ExecFinished = "compute.exec.finished";
    public const string SnapshotCaptured = "compute.snapshot.captured";
}
