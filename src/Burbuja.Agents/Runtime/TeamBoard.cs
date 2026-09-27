using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Teams;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Runtime;

public sealed record TeamTaskResult(bool Ok, TeamTaskSnapshot? Task, ArmError? Error, bool WriteScopeWarning);

/// <summary>
/// The task board with compare-and-swap. The store is the authority; every mutation also appends an
/// event. `blockedBy` must stay acyclic, write scopes are advisory (a warning, never a lock), and
/// deletions leave a tombstone rather than a hole.
/// </summary>
public sealed class TeamBoard(IResourceStore store, IEventLog events, IClock clock)
{
    public static string TaskKey(string teamKey, string taskId) => $"{teamKey.TrimEnd('/')}/tasks/{taskId}".ToLowerInvariant();

    public async ValueTask<TeamTaskSnapshot?> GetTaskAsync(string teamKey, string taskId, CancellationToken ct)
    {
        var env = await store.GetAsync(TaskKey(teamKey, taskId), ct);
        return env is null ? null : Map(env);
    }

    public async ValueTask<IReadOnlyList<TeamTaskSnapshot>> ListTasksAsync(string teamKey, CancellationToken ct)
    {
        var page = await store.ListAsync(new ResourceStoreQuery(null, null, null, null, 1000, null, teamKey.TrimEnd('/') + "/tasks/"), ct);
        return page.Value.Select(Map).Where(t => !t.Deleted).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    public async ValueTask<TeamTaskResult> PutTaskAsync(string teamKey, string taskId, TeamTaskProperties desired, CancellationToken ct)
    {
        var tasks = await ListTasksAsync(teamKey, ct);
        if (tasks.Count >= TeamBounds.BoardMax)
            return Fail("BoardFull", $"the board holds at most {TeamBounds.BoardMax} tasks.");

        if (!IsAcyclic(taskId, desired.BlockedBy, tasks, out var cycle))
            return Fail("BoardCycle", $"blockedBy introduces a cycle: {string.Join(" -> ", cycle)}");

        var existing = await store.GetAsync(TaskKey(teamKey, taskId), ct);
        desired.Revision = existing is null ? 1 : desired.Revision + 1;
        desired.Deleted = false;
        var stored = await UpsertAsync(teamKey, taskId, desired, existing, ct);
        await EmitAsync(teamKey, taskId, stored, ct);
        return new TeamTaskResult(true, stored, null, false);
    }

    /// <summary>A CAS patch: a caller that does not hold the current revision gets 412, never a lost update.</summary>
    public async ValueTask<TeamTaskResult> PatchTaskAsync(
        string teamKey,
        string taskId,
        int? expectedRevision,
        string? ifMatch,
        Action<TeamTaskProperties> mutate,
        CancellationToken ct)
    {
        var raw = await store.GetAsync(TaskKey(teamKey, taskId), ct);
        if (raw is null)
            return Fail("NotFound", $"task '{taskId}' was not found.");
        var current = raw.Properties.Deserialize<TeamTaskProperties>(JsonWire.Options) ?? new TeamTaskProperties();
        if (expectedRevision is { } expected && current.Revision != expected)
            return Fail("TaskRevisionConflict", $"the task is at revision {current.Revision}, not {expected}.");

        mutate(current);
        current.Revision = expectedRevision.GetValueOrDefault(current.Revision) + 1;
        var others = (await ListTasksAsync(teamKey, ct)).Where(t => t.Id != taskId).ToList();
        if (!IsAcyclic(taskId, current.BlockedBy, others, out var cycle))
            return Fail("BoardCycle", $"blockedBy introduces a cycle: {string.Join(" -> ", cycle)}");

        var warning = OverlapsOtherOwner(others, taskId, current);
        try
        {
            var stored = await UpsertAsync(teamKey, taskId, current, raw, ct, ifMatch);
            await EmitAsync(teamKey, taskId, stored, ct, warning);
            return new TeamTaskResult(true, stored, null, warning);
        }
        catch (ArmException ex) when (ex.Error.HttpStatus == 412)
        {
            // Someone else won the CAS; surface the conflict rather than silently retrying.
            return Fail("TaskRevisionConflict", "the task changed while patching.");
        }
    }

    public async ValueTask<TeamTaskResult> DeleteTaskAsync(string teamKey, string taskId, CancellationToken ct)
    {
        var raw = await store.GetAsync(TaskKey(teamKey, taskId), ct);
        if (raw is null)
            return Fail("NotFound", $"task '{taskId}' was not found.");
        var current = raw.Properties.Deserialize<TeamTaskProperties>(JsonWire.Options) ?? new TeamTaskProperties();
        current.Deleted = true;
        current.Status = TeamTaskStatus.Cancelled;
        current.Revision += 1;
        var stored = await UpsertAsync(teamKey, taskId, current, raw, ct);
        await EmitAsync(teamKey, taskId, stored, ct);
        return new TeamTaskResult(true, stored, null, false);
    }

    public static bool IsAcyclic(
        string taskId,
        IReadOnlyList<string> blockedBy,
        IReadOnlyList<TeamTaskSnapshot> existing,
        out IReadOnlyList<string> cycle)
    {
        var edges = existing
            .Where(t => t.Id != taskId)
            .ToDictionary(t => t.Id, t => (IReadOnlyList<string>)t.BlockedBy, StringComparer.Ordinal);
        edges[taskId] = blockedBy ?? [];
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        bool Dfs(string id)
        {
            if (visiting.Contains(id))
            {
                path.Add(id);
                return true;
            }

            if (done.Contains(id) || !edges.TryGetValue(id, out var next))
                return false;
            visiting.Add(id);
            path.Add(id);
            foreach (var dep in next)
            {
                if (Dfs(dep))
                    return true;
            }

            visiting.Remove(id);
            path.RemoveAt(path.Count - 1);
            done.Add(id);
            return false;
        }

        foreach (var id in edges.Keys)
        {
            path.Clear();
            if (Dfs(id))
            {
                cycle = path;
                return false;
            }
        }

        cycle = [];
        return true;
    }

    private static bool OverlapsOtherOwner(IReadOnlyList<TeamTaskSnapshot> tasks, string taskId, TeamTaskProperties current)
    {
        if (current.WriteScopes.Count == 0)
            return false;
        foreach (var other in tasks)
        {
            if (other.Id == taskId || other.OwnerAgentId is null || other.OwnerAgentId == current.OwnerAgentId)
                continue;
            if (other.WriteScopes.Intersect(current.WriteScopes, StringComparer.Ordinal).Any())
                return true;
        }

        return false;
    }

    private async ValueTask<TeamTaskSnapshot> UpsertAsync(
        string teamKey,
        string taskId,
        TeamTaskProperties properties,
        ResourceEnvelope<JsonObject>? existing,
        CancellationToken ct,
        string? ifMatchOverride = null)
    {
        var id = TaskKey(teamKey, taskId);
        var envelope = new ResourceEnvelope<JsonObject>(
            $"{teamKey.TrimEnd('/')}/tasks/{taskId}",
            taskId,
            $"{AgentsConstants.Namespace}/teams/tasks",
            "local",
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToNode(properties, JsonWire.Options)!.AsObject(),
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
            existing?.ETag ?? "");
        var stored = await store.UpsertAsync(envelope, ifMatchOverride ?? existing?.ETag, ct);
        return Map(stored);
    }

    private async ValueTask EmitAsync(string teamKey, string taskId, TeamTaskSnapshot task, CancellationToken ct, bool warning = false)
    {
        await events.AppendAsync(new EventAppend(
            $"{teamKey.TrimEnd('/')}/stream",
            taskId,
            Abstractions.Agents.AgentTeamEvents.TaskChanged,
            task.Status,
            new JsonObject
            {
                ["taskId"] = task.Id,
                ["status"] = task.Status,
                ["revision"] = task.Revision,
                ["ownerAgentId"] = task.OwnerAgentId,
                ["blockedBy"] = new JsonArray(task.BlockedBy.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["deleted"] = task.Deleted,
                ["writeScopeWarning"] = warning,
            },
            RenderLane.Output | RenderLane.Log,
            taskId,
            null,
            AgentsConstants.SessionStreamType), ct);
    }

    private static TeamTaskResult Fail(string code, string message) =>
        new(false, null, new ArmError(code, message, null, [], null, code switch
        {
            "NotFound" => 404,
            "TaskRevisionConflict" => 412,
            _ => 400,
        }), false);

    private static TeamTaskSnapshot Map(ResourceEnvelope<JsonObject> env)
    {
        var props = env.Properties.Deserialize<TeamTaskProperties>(JsonWire.Options) ?? new TeamTaskProperties();
        return new TeamTaskSnapshot(
            env.Name,
            props.Title,
            props.Status,
            props.Revision,
            props.BlockedBy,
            props.OwnerAgentId,
            props.WriteScopes,
            props.Deleted);
    }
}

/// <summary>`fold(team stream)` must equal the store projection — the equivalence is tested.</summary>
public static class TeamFolder
{
    public static IReadOnlyList<TeamTaskSnapshot> Fold(IEnumerable<LedgerEvent> events)
    {
        var tasks = new Dictionary<string, TeamTaskSnapshot>(StringComparer.Ordinal);
        foreach (var evt in events.OrderBy(e => e.Seq))
        {
            if (evt.Type != Abstractions.Agents.AgentTeamEvents.TaskChanged || evt.Payload is not JsonObject payload)
                continue;
            var id = payload["taskId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
                continue;
            var revision = payload["revision"]?.GetValue<int>() ?? 0;
            if (tasks.TryGetValue(id, out var previous) && previous.Revision > revision)
                continue;
            tasks[id] = new TeamTaskSnapshot(
                id,
                id,
                payload["status"]?.GetValue<string>() ?? TeamTaskStatus.Pending,
                revision,
                (payload["blockedBy"] as JsonArray ?? []).Select(v => v?.GetValue<string>() ?? "").ToList(),
                payload["ownerAgentId"]?.GetValue<string>(),
                []);
        }

        return tasks.Values.Where(t => !IsDeleted(t)).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    private static bool IsDeleted(TeamTaskSnapshot task) => task.Status == TeamTaskStatus.Cancelled;
}
