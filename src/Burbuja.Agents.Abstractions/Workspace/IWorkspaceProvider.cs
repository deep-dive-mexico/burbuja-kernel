namespace Burbuja.Agents.Abstractions.Workspace;

public sealed record WorkspaceHandle(string JobId, string Path, string VolumeId, string Branch, string BaseSha);

public sealed record WorkspacePatch(string Diff, string Diffstat);

public interface IWorkspaceProvider
{
    ValueTask<WorkspaceHandle> OpenAsync(string jobId, string repoId, string baseRef, CancellationToken ct);
    ValueTask<WorkspacePatch> CaptureDiffAsync(WorkspaceHandle handle, CancellationToken ct);
    ValueTask CommitAsync(WorkspaceHandle handle, string message, CancellationToken ct);
    ValueTask ReattachAsync(WorkspaceHandle handle, CancellationToken ct);
}
