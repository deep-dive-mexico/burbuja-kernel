using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Abstractions.Workspace;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Process;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Tools.Workspace;

public sealed class WorkspaceToolsPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Tools.Workspace",
        "Workspace tools",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Tools.Workspace.dll",
        typeof(WorkspaceToolsPlugin).FullName!,
        PluginLoadKind.Static,
        ["tools.workspace"],
        ["agents.tool"],
        [PluginCapability.ProcessExecution, PluginCapability.FilesystemWrite],
        ["all", "worker"],
        null,
        true,
        [],
        320);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IWorkspaceProvider, GitWorkspaceProvider>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, WriteFileTool>();
        services.AddSingleton<IAgentTool, EditFileTool>();
        services.AddSingleton<IAgentTool, ListDirTool>();
        services.AddSingleton<IAgentTool, BashTool>();
    }
}

internal static class WorkspacePaths
{
    public static string Resolve(string workRoot, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(workRoot, relative.Replace('\\', '/')));
        var root = Path.GetFullPath(workRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.Ordinal) && !string.Equals(combined, Path.GetFullPath(workRoot), StringComparison.Ordinal))
            throw new InvalidOperationException("path escapes work root");
        return combined;
    }

    public static JsonObject Schema(params string[] required) =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = true,
            ["required"] = new JsonArray(required.Select(r => JsonValue.Create(r)).ToArray()),
        };
}

public sealed class ReadFileTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new("read_file", "Read a file", WorkspacePaths.Schema("path"), true, false, true, "fs.read", []);

    public ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = WorkspacePaths.Resolve(context.WorkRoot, input["path"]?.GetValue<string>() ?? "");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        return new(new ToolResult(JsonValue.Create(text)!, !File.Exists(path)));
    }
}

public sealed class WriteFileTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new("write_file", "Write a file", WorkspacePaths.Schema("path", "content"), true, false, true, "fs.write", ["path"]);

    public ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = WorkspacePaths.Resolve(context.WorkRoot, input["path"]?.GetValue<string>() ?? "");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? context.WorkRoot);
        File.WriteAllText(path, input["content"]?.GetValue<string>() ?? "");
        return new(new ToolResult(JsonValue.Create("ok")!, false));
    }
}

public sealed class EditFileTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new("edit_file", "Edit a file", WorkspacePaths.Schema("path"), true, false, true, "fs.write", ["path"]);

    public ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = WorkspacePaths.Resolve(context.WorkRoot, input["path"]?.GetValue<string>() ?? "");
        if (!File.Exists(path))
            return new(new ToolResult(JsonValue.Create("missing")!, true));
        var old = input["old"]?.GetValue<string>() ?? "";
        var next = input["new"]?.GetValue<string>() ?? "";
        var text = File.ReadAllText(path);
        File.WriteAllText(path, string.IsNullOrEmpty(old) ? next : text.Replace(old, next, StringComparison.Ordinal));
        return new(new ToolResult(JsonValue.Create("ok")!, false));
    }
}

public sealed class ListDirTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new("list_dir", "List a directory", WorkspacePaths.Schema(), true, false, true, "fs.read", []);

    public ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rel = input["path"]?.GetValue<string>() ?? ".";
        var path = WorkspacePaths.Resolve(context.WorkRoot, rel);
        if (!Directory.Exists(path))
            return new(new ToolResult(new JsonArray(), true));
        var names = Directory.GetFileSystemEntries(path).Select(p => JsonValue.Create(Path.GetFileName(p))).ToArray();
        return new(new ToolResult(new JsonArray(names!), false));
    }
}

public sealed class BashTool(IProcessRunner processes) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new("bash", "Run a shell command", WorkspacePaths.Schema("command"), false, false, false, "bash", []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        var command = input["command"]?.GetValue<string>() ?? "";
        var cwdRel = input["cwd"]?.GetValue<string>() ?? ".";
        var cwd = WorkspacePaths.Resolve(context.WorkRoot, cwdRel);
        var result = await processes.RunAsync(
            new ProcessStartRequest("/bin/bash", ["-lc", command], cwd, new Dictionary<string, string>(), context.Timeout, 64 * 1024 * 1024, true),
            pass.Grant,
            ct);
        var payload = new JsonObject
        {
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.StandardOutput,
            ["stderr"] = result.StandardError,
            ["timedOut"] = result.TimedOut,
        };
        return new ToolResult(payload, result.TimedOut || result.ExitCode != 0);
    }
}

public sealed class GitWorkspaceProvider(IProcessRunner processes, IExecutionGrantAuthority grants) : IWorkspaceProvider
{
    public async ValueTask<WorkspaceHandle> OpenAsync(string jobId, string repoId, string baseRef, CancellationToken ct)
    {
        var slug = jobId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "job";
        var root = Path.Combine(Path.GetTempPath(), "burbuja-git", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(repo);
        var grant = grants.Mint("workspace:" + jobId, "workspace.open", TimeSpan.FromMinutes(2));
        await Git(repo, grant, ct, "init", "-b", "main");
        await Git(repo, grant, ct, "config", "user.email", "agent@burbuja.test");
        await Git(repo, grant, ct, "config", "user.name", "burbuja");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "demo\n", ct);
        await Git(repo, grant, ct, "add", "README.md");
        await Git(repo, grant, ct, "commit", "-m", "init");
        var sha = (await Git(repo, grant, ct, "rev-parse", "HEAD")).StandardOutput.Trim();
        Directory.CreateDirectory(work);
        var branch = "agent/" + slug;
        await Git(repo, grant, ct, "worktree", "add", "-b", branch, work, baseRef.Length == 0 ? "main" : baseRef);
        return new WorkspaceHandle(jobId, work, root, branch, sha);
    }

    public async ValueTask<WorkspacePatch> CaptureDiffAsync(WorkspaceHandle handle, CancellationToken ct)
    {
        var grant = grants.Mint("workspace:" + handle.JobId, "workspace.diff", TimeSpan.FromMinutes(1));
        var diff = await Git(handle.Path, grant, ct, "diff");
        var stat = await Git(handle.Path, grant, ct, "diff", "--stat");
        return new WorkspacePatch(diff.StandardOutput, stat.StandardOutput);
    }

    public async ValueTask CommitAsync(WorkspaceHandle handle, string message, CancellationToken ct)
    {
        var grant = grants.Mint("workspace:" + handle.JobId, "workspace.commit", TimeSpan.FromMinutes(1));
        await Git(handle.Path, grant, ct, "add", "-A");
        var status = await Git(handle.Path, grant, ct, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
            return;
        await Git(handle.Path, grant, ct, "-c", "user.email=agent@burbuja.test", "-c", "user.name=burbuja", "commit", "-m", message);
    }

    public ValueTask ReattachAsync(WorkspaceHandle handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return default;
    }

    private async Task<ProcessResult> Git(string cwd, ExecutionGrant grant, CancellationToken ct, params string[] args) =>
        await processes.RunAsync(new ProcessStartRequest("git", args, cwd, new Dictionary<string, string>(), TimeSpan.FromSeconds(30), 4 * 1024 * 1024, true), grant, ct);
}
