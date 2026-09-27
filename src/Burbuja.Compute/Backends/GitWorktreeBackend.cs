using System.Text.Json.Nodes;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Sdk.Policy;
using Microsoft.Extensions.Options;

namespace Burbuja.Compute.Backends;

/// <summary>
/// A workspace that is a git worktree of a scratch repo (or of an allow-listed remote). Every git
/// command goes through the policy pipeline first and runs under a minted execution grant.
/// </summary>
public sealed class GitWorktreeBackend(
    IOptions<ComputeOptions> options,
    IProcessRunner processes,
    IExecutionGrantAuthority grants,
    IArtifactStore artifacts,
    IRedactor redactor,
    IPolicyPipeline<ActionPolicySubject> policy) : IComputeBackend
{
    public string Kind => "git-worktree";

    public IReadOnlyList<string> SupportedImages { get; } = ["git"];

    public IReadOnlyList<string> SupportedSizes { get; } = ["small"];

    public string ResolveRootPath(string workspaceKey) =>
        Path.Combine(BaseRoot(), BackendSupport.Sanitize(workspaceKey), "work");

    public async ValueTask<ComputeProvisionResult> ProvisionAsync(ComputeProvisionRequest request, CancellationToken ct)
    {
        var name = BackendSupport.Sanitize(request.WorkspaceKey);
        var dir = Path.Combine(BaseRoot(), name);
        var repo = Path.Combine(dir, "repo");
        var work = Path.Combine(dir, "work");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var grant = grants.Mint(request.WorkspaceKey, "compute:provision:" + name, TimeSpan.FromMinutes(5));
        var remote = request.Spec.Repo.Remote;
        if (!string.IsNullOrWhiteSpace(remote))
        {
            if (!IsAllowedRemote(remote))
                throw new InvalidOperationException($"remote '{remote}' is not in Compute:Git:AllowedRemotes");
            Gate(remote, "clone", request.WorkspaceKey);
            await Git(null, grant, ct, "clone", remote, repo);
        }
        else
        {
            Directory.CreateDirectory(repo);
            await Git(repo, grant, ct, "init", "-b", "main");
            await Git(repo, grant, ct, "config", "user.email", "compute@burbuja.test");
            await Git(repo, grant, ct, "config", "user.name", "burbuja-compute");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "burbuja compute workspace\n", ct);
            await Git(repo, grant, ct, "add", "README.md");
            await Git(repo, grant, ct, "commit", "-m", "init");
        }

        var sha = (await Git(repo, grant, ct, "rev-parse", "HEAD")).StandardOutput.Trim();
        var prefix = request.Spec.Repo.BranchPrefix ?? "compute/";
        var branch = prefix + name;
        var baseRef = string.IsNullOrWhiteSpace(request.Spec.Repo.BaseRef) ? "HEAD" : request.Spec.Repo.BaseRef;
        await Git(repo, grant, ct, "worktree", "add", "-b", branch, work, baseRef);
        return new ComputeProvisionResult($"git:{work}", work, branch, sha);
    }

    public ValueTask<ComputeExecResult> ExecAsync(ComputeExecRequest request, ExecutionGrant grant, CancellationToken ct) =>
        BackendSupport.RunBashAsync(processes, redactor, request, grant, ct);

    public ValueTask<ComputeSnapshotResult> SnapshotAsync(string rootPath, string name, CancellationToken ct) =>
        BackendSupport.ZipAsync(rootPath, name, artifacts, ct);

    public ValueTask DestroyAsync(string rootPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = BaseRoot();
        if (!BackendSupport.IsInside(root, rootPath) && !string.Equals(Path.GetFullPath(rootPath), root, StringComparison.Ordinal))
            throw new InvalidOperationException($"destroy path '{rootPath}' escapes {root}");
        // Remove the whole workspace directory (repo + worktree); a lingering registration in the
        // repo is irrelevant because the repo goes with it.
        var dir = Path.GetDirectoryName(Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar));
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        return default;
    }

    public ValueTask<ComputeProbe> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = BaseRoot();
        Directory.CreateDirectory(root);
        return new(new ComputeProbe(Directory.Exists(root), Kind));
    }

    private bool IsAllowedRemote(string remote) =>
        options.Value.Git.AllowedRemotes.Any(prefix => remote.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private void Gate(string remote, string command, string workspaceKey)
    {
        var input = new JsonObject { ["command"] = command, ["remote"] = remote };
        var context = new PolicyContext(workspaceKey, BaseRoot(), BaseRoot(), PolicyProfileParser.Parse(options.Value.Profile), "local", BaseRoot(), [], [], []);
        var verdict = policy.Classify(new ActionPolicySubject("git", input, context));
        if (verdict.Tier == RiskTier.HardDeny)
            throw new InvalidOperationException($"PolicyDenied: {verdict.Finding.Reason}");
    }

    private async Task<ProcessResult> Git(string? cwd, ExecutionGrant grant, CancellationToken ct, params string[] args) =>
        await processes.RunAsync(
            new ProcessStartRequest("git", args, cwd ?? BaseRoot(), new Dictionary<string, string>(), TimeSpan.FromSeconds(60), 4 * 1024 * 1024, true),
            grant,
            ct);

    private string BaseRoot()
    {
        var configured = options.Value.Git.Root;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Path.Combine(Path.GetTempPath(), "burbuja-compute-git");
        return Path.GetFullPath(configured);
    }
}
