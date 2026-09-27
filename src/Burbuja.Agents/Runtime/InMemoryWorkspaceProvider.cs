using System.Collections.Concurrent;
using Burbuja.Agents.Abstractions.Workspace;

namespace Burbuja.Agents.Runtime;

public sealed class InMemoryWorkspaceProvider : IWorkspaceProvider
{
    private readonly ConcurrentDictionary<string, Mutable> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> CommitsFor(string jobId) =>
        _workspaces.TryGetValue(jobId, out var w) ? w.Commits.ToArray() : [];

    public string? BranchFor(string jobId) =>
        _workspaces.TryGetValue(jobId, out var w) ? w.Branch : null;

    public ValueTask<WorkspaceHandle> OpenAsync(string jobId, string repoId, string baseRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var slug = jobId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "job";
        var branch = "agent/" + slug;
        var root = Path.Combine(Path.GetTempPath(), "burbuja-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var handle = new WorkspaceHandle(jobId, root, "inmem", branch, "base");
        _workspaces[jobId] = new Mutable(handle, baseRef);
        return new(handle);
    }

    public ValueTask<WorkspacePatch> CaptureDiffAsync(WorkspaceHandle handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_workspaces.TryGetValue(handle.JobId, out var w))
            return new(new WorkspacePatch("", ""));
        return new(new WorkspacePatch(w.Diff, $"{w.Files.Count} files"));
    }

    public ValueTask CommitAsync(WorkspaceHandle handle, string message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var w = _workspaces.GetOrAdd(handle.JobId, _ => new Mutable(handle, "main"));
        foreach (var file in Directory.Exists(handle.Path) ? Directory.GetFiles(handle.Path, "*", SearchOption.AllDirectories) : [])
        {
            var rel = Path.GetRelativePath(handle.Path, file);
            w.Files[rel] = File.ReadAllText(file);
            w.Diff += $"diff --git a/{rel} b/{rel}\n+{w.Files[rel]}\n";
        }

        w.Commits.Add(message);
        return default;
    }

    public ValueTask ReattachAsync(WorkspaceHandle handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return default;
    }

    private sealed class Mutable(WorkspaceHandle handle, string baseBranch)
    {
        public WorkspaceHandle Handle { get; } = handle;
        public string BaseBranch { get; } = baseBranch;
        public string Branch { get; } = handle.Branch;
        public List<string> Commits { get; } = [];
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public string Diff { get; set; } = "";
    }
}
