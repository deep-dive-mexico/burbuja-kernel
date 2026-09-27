using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Teams;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Handlers;

public sealed class TeamHandler(
    IResourceStore store,
    IClock clock,
    IEventLog events) : EnvelopeHandler<TeamProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/teams";

    public override async ValueTask<PutOutcome<TeamProperties>> PutAsync(ResourceRequest req, TeamProperties desired, CancellationToken ct)
    {
        if (desired.Members.Count > TeamBounds.RosterMax)
            return new PutOutcome<TeamProperties>.Rejected(new ArmError(
                "RosterFull",
                $"a team holds at most {TeamBounds.RosterMax} members.",
                "/properties/members",
                [],
                null,
                400));
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var action = req.Action.ToLowerInvariant();
        return action switch
        {
            "spawnmember" => await SpawnMemberAsync(req, ct),
            "waitforchange" => await WaitForChangeAsync(req, ct),
            "fold" => await FoldAsync(req, ct),
            _ => await base.InvokeAsync(req, ct),
        };
    }

    private async ValueTask<ActionOutcome> SpawnMemberAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var raw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (raw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var team = MapTeam(raw);
        var member = req.Body?["agentId"]?.GetValue<string>() ?? "";
        if (member.Length == 0)
            return new ActionOutcome.Rejected(ArmError.Validation("agentId is required.", []));
        if (team.Properties.Members.Count >= TeamBounds.RosterMax && !team.Properties.Members.Contains(member, StringComparer.Ordinal))
            return new ActionOutcome.Rejected(new ArmError("RosterFull", $"a team holds at most {TeamBounds.RosterMax} members.", null, [], null, 400));
        if (!team.Properties.Members.Contains(member, StringComparer.Ordinal))
            team.Properties.Members.Add(member);

        var json = new ResourceEnvelope<JsonObject>(
            team.Id, team.Name, team.Type, team.Location, team.Tags,
            JsonSerializer.SerializeToNode(team.Properties, JsonWire.Options)!.AsObject(),
            team.ProvisioningState, team.SystemData, team.ETag);
        await Store.UpsertAsync(json, team.ETag, ct);
        await EmitAsync(team.Id, AgentTeamEvents.MemberAdded, member, ct);
        return new ActionOutcome.Ok(new JsonObject { ["members"] = new JsonArray(team.Properties.Members.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()) });
    }

    private async ValueTask<ActionOutcome> WaitForChangeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var seconds = req.Body?["timeoutSeconds"]?.GetValue<int>() ?? 30;
        var timeout = TimeSpan.FromSeconds(seconds);
        if (!WaitForChangeBounds.IsValid(timeout))
            return new ActionOutcome.Rejected(new ArmError(
                "TimeoutOutOfRange",
                $"timeoutSeconds must be between {WaitForChangeBounds.Floor.TotalSeconds} and {WaitForChangeBounds.Ceiling.TotalSeconds}.",
                null,
                [],
                null,
                400));

        var streamId = $"{StoreKey(req.Id)}/stream";
        var changed = new List<string>();
        var timedOut = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await foreach (var evt in events.TailAsync(streamId, 0, cts.Token))
            {
                if (evt.Type != AgentTeamEvents.TaskChanged)
                    continue;
                var id = evt.Payload?["taskId"]?.GetValue<string>();
                if (id is not null)
                    changed.Add(id);
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }

        return new ActionOutcome.Ok(new JsonObject
        {
            ["timedOut"] = timedOut,
            ["atSeq"] = 0,
            ["changed"] = new JsonArray(changed.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        });
    }

    private async ValueTask<ActionOutcome> FoldAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var teamKey = StoreKey(req.Id);
        var streamId = $"{teamKey}/stream";
        var seen = new List<LedgerEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var evt in events.TailAsync(streamId, 0, cts.Token))
                seen.Add(evt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        var folded = TeamFolder.Fold(seen);
        return new ActionOutcome.Ok(new JsonObject
        {
            ["tasks"] = new JsonArray(folded.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id,
                ["status"] = t.Status,
                ["revision"] = t.Revision,
            }).ToArray()),
        });
    }

    private async ValueTask EmitAsync(string canonicalId, string type, string subject, CancellationToken ct) =>
        await events.AppendAsync(new EventAppend(
            $"{canonicalId.ToLowerInvariant()}/stream",
            subject,
            type,
            subject,
            null,
            RenderLane.Output | RenderLane.Log,
            subject,
            null,
            AgentsConstants.SessionStreamType), ct);

    private static ResourceEnvelope<TeamProperties> MapTeam(ResourceEnvelope<JsonObject> env) =>
        new(env.Id, env.Name, env.Type, env.Location, env.Tags,
            env.Properties.Deserialize<TeamProperties>(JsonWire.Options) ?? new TeamProperties(),
            env.ProvisioningState, env.SystemData, env.ETag);
}

public sealed class TeamTaskHandler(
    IResourceStore store,
    IClock clock,
    TeamBoard board) : ChildEnvelopeHandler<TeamTaskProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/teams/tasks";

    public override async ValueTask<PutOutcome<TeamTaskProperties>> PutAsync(ResourceRequest req, TeamTaskProperties desired, CancellationToken ct)
    {
        var taskId = req.Id.NameSegments.LastOrDefault() ?? "";
        var result = await board.PutTaskAsync(TeamKey(req.Id), taskId, desired, ct);
        return result.Ok
            ? new PutOutcome<TeamTaskProperties>.Updated(MapTask(result.Task!))
            : new PutOutcome<TeamTaskProperties>.Rejected(result.Error!);
    }

    public override async ValueTask<PutOutcome<TeamTaskProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct)
    {
        var revision = patch.Properties?["revision"]?.GetValue<int>();
        if (revision is null && string.IsNullOrWhiteSpace(req.IfMatch) && !patch.Properties!.ContainsKey("revision"))
            return new PutOutcome<TeamTaskProperties>.Rejected(new ArmError(
                "RevisionRequired",
                "a PATCH requires properties.revision or If-Match.",
                null,
                [],
                null,
                400));

        var taskId = req.Id.NameSegments.LastOrDefault() ?? "";
        var properties = patch.Properties ?? new JsonObject();
        var result = await board.PatchTaskAsync(TeamKey(req.Id), taskId, revision, req.IfMatch, current =>
        {
            if (properties["title"]?.GetValue<string>() is { Length: > 0 } title)
                current.Title = title;
            if (properties["status"]?.GetValue<string>() is { Length: > 0 } status)
                current.Status = status;
            if (properties["ownerAgentId"] is { } owner)
                current.OwnerAgentId = owner.GetValue<string>();
            if (properties["blockedBy"] is JsonArray blocked)
                current.BlockedBy = blocked.Select(b => b?.GetValue<string>() ?? "").Where(b => b.Length > 0).ToList();
            if (properties["writeScopes"] is JsonArray scopes)
                current.WriteScopes = scopes.Select(s => s?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }, ct);

        return result.Ok
            ? new PutOutcome<TeamTaskProperties>.Updated(MapTask(result.Task!))
            : new PutOutcome<TeamTaskProperties>.Rejected(result.Error!);
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var taskId = req.Id.NameSegments.LastOrDefault() ?? "";
        var result = await board.DeleteTaskAsync(TeamKey(req.Id), taskId, ct);
        return result.Ok ? new DeleteOutcome.Deleted() : new DeleteOutcome.Rejected(result.Error!);
    }

    public override async ValueTask<PagedResult<ResourceEnvelope<TeamTaskProperties>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var tasks = await board.ListTasksAsync(TeamKey(req.Id), ct);
        var list = new List<ResourceEnvelope<TeamTaskProperties>>();
        foreach (var task in tasks)
        {
            var env = await Store.GetAsync(TeamBoard.TaskKey(TeamKey(req.Id), task.Id), ct);
            if (env is not null)
                list.Add(MapTask(env));
        }

        return new PagedResult<ResourceEnvelope<TeamTaskProperties>>(list, null);
    }

    private static string TeamKey(ResourceId id)
    {
        var canonical = id.Canonical;
        var marker = "/tasks/";
        var idx = canonical.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return (idx < 0 ? canonical : canonical[..idx]).ToLowerInvariant();
    }

    private static ResourceEnvelope<TeamTaskProperties> MapTask(ResourceEnvelope<JsonObject> env) =>
        new(env.Id, env.Name, env.Type, env.Location, env.Tags,
            env.Properties.Deserialize<TeamTaskProperties>(JsonWire.Options) ?? new TeamTaskProperties(),
            env.ProvisioningState, env.SystemData, env.ETag);

    private static ResourceEnvelope<TeamTaskProperties> MapTask(TeamTaskSnapshot task) =>
        new(
            task.Id,
            task.Id,
            $"{AgentsConstants.Namespace}/teams/tasks",
            "local",
            new Dictionary<string, string>(),
            new TeamTaskProperties
            {
                Title = task.Title,
                Status = task.Status,
                Revision = task.Revision,
                BlockedBy = task.BlockedBy.ToList(),
                OwnerAgentId = task.OwnerAgentId,
                WriteScopes = task.WriteScopes.ToList(),
            },
            ProvisioningState.Succeeded,
            new SystemData(DateTimeOffset.UnixEpoch, "system", "System", DateTimeOffset.UnixEpoch, "system", "System"),
            "");
}

public sealed class TeamMessageHandler(IResourceStore store, IClock clock) : ChildEnvelopeHandler<TeamMessageProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/teams/messages";

    public override async ValueTask<PutOutcome<TeamMessageProperties>> PutAsync(ResourceRequest req, TeamMessageProperties desired, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(desired.Body);
        if (bytes > TeamBounds.MessageMaxBytes)
            return new PutOutcome<TeamMessageProperties>.Rejected(new ArmError(
                "MessageTooLarge",
                $"a message is at most {TeamBounds.MessageMaxBytes} bytes.",
                null,
                [],
                null,
                400));

        // A duplicate dedup key is held by the target mailbox and never re-delivered.
        var page = await Store.ListAsync(new ResourceStoreQuery(null, null, null, null, 1000, null, null), ct);
        if (desired.DedupKey is { Length: > 0 } key
            && page.Value.Any(e => e.Properties["dedupKey"]?.GetValue<string>() == key))
            return new PutOutcome<TeamMessageProperties>.Updated(new ResourceEnvelope<TeamMessageProperties>(
                req.Id.Canonical,
                req.Id.NameSegments.LastOrDefault() ?? "",
                TypeName,
                "local",
                new Dictionary<string, string>(),
                desired,
                ProvisioningState.Succeeded,
                new SystemData(Clock.UtcNow, "system", "System", Clock.UtcNow, "system", "System"),
                ""));

        return await base.PutAsync(req, desired, ct);
    }
}
