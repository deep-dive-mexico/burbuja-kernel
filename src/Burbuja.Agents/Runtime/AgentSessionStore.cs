using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Runtime;

/// <summary>Sessions are resources: a session outlives jobs, is forkable, and has an inbox.</summary>
public sealed class AgentSessionStore(IResourceStore store, IClock clock)
{
    public static string SessionKey(string agentKey, string sessionId) =>
        $"{agentKey.TrimEnd('/')}/sessions/{sessionId}".ToLowerInvariant();

    public async ValueTask<ResourceEnvelope<SessionProperties>?> GetAsync(string key, CancellationToken ct)
    {
        var env = await store.GetAsync(key.ToLowerInvariant(), ct);
        return env is null ? null : Map(env);
    }

    /// <summary>
    /// The session an agent is currently using, created on first use. Agents are inert until
    /// something activates them, and M5 has two activators — the kernel's own worker and an MCP
    /// caller driving an external agent — so the "create if absent" rule lives here once.
    /// </summary>
    public async ValueTask<(ResourceEnvelope<SessionProperties> Session, bool Created)> EnsureAsync(
        ResourceEnvelope<AgentProperties> agent,
        string agentKey,
        CancellationToken ct)
    {
        if (agent.Properties.CurrentSessionId is { Length: > 0 } current
            && await GetAsync(SessionKey(agent.Id, current), ct) is { } existing)
            return (existing, false);

        var sessionId = "session-" + Guid.NewGuid().ToString("N")[..12];
        var created = await CreateAsync(agent.Id, agent.Name, sessionId, null, null, null, agent.Properties.TeamId, ct);
        return (created, true);
    }

    public async ValueTask<ResourceEnvelope<SessionProperties>> CreateAsync(
        string agentId,
        string agentName,
        string sessionId,
        string? forkedFrom,
        long? forkedAtSeq,
        string? parentSessionId,
        string? teamId,
        CancellationToken ct)
    {
        var id = $"{agentId.TrimEnd('/')}/sessions/{sessionId}";
        var properties = new SessionProperties
        {
            AgentId = agentName,
            StreamId = AgentsConstants.SessionStreamId(id),
            ForkedFromSessionId = forkedFrom,
            ForkedAtSeq = forkedAtSeq,
            EndSeedSeq = forkedAtSeq ?? 0,
            ParentSessionId = parentSessionId,
            TeamId = teamId,
            LastSeq = forkedAtSeq ?? 0,
        };
        var envelope = new ResourceEnvelope<JsonObject>(
            id,
            sessionId,
            $"{AgentsConstants.Namespace}/agents/sessions",
            "local",
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToNode(properties, JsonWire.Options)!.AsObject(),
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
            "");
        return Map(await store.UpsertAsync(envelope, null, ct));
    }

    public async ValueTask<ResourceEnvelope<SessionProperties>> UpdateAsync(
        string key,
        Action<SessionProperties> mutate,
        CancellationToken ct)
    {
        ArmException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var env = await GetAsync(key, ct) ?? throw new InvalidOperationException(key);
            mutate(env.Properties);
            var json = new ResourceEnvelope<JsonObject>(
                env.Id,
                env.Name,
                env.Type,
                env.Location,
                env.Tags,
                JsonSerializer.SerializeToNode(env.Properties, JsonWire.Options)!.AsObject(),
                env.ProvisioningState,
                env.SystemData,
                env.ETag);
            try
            {
                return Map(await store.UpsertAsync(json, env.ETag, ct));
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412)
            {
                last = ex;
            }
        }

        throw last ?? new ArmException(ArmError.PreconditionFailed("Could not persist session state."));
    }

    private static ResourceEnvelope<SessionProperties> Map(ResourceEnvelope<JsonObject> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            env.Properties.Deserialize<SessionProperties>(JsonWire.Options) ?? new SessionProperties(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);
}
