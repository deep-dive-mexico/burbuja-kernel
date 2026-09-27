using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// Reading and writing the <c>agents</c> resource with its ETag discipline. A stateful agent is
/// held in memory for a whole tool loop (the activation worker) or for the length of an MCP call,
/// so a blind overwrite would erase an inbox item that arrived while the model was thinking. Two
/// callers share this — the activation worker and the kernel MCP server — and they must not
/// disagree about what "save the agent" means.
/// </summary>
public static class AgentPersistence
{
    public static async ValueTask<ResourceEnvelope<AgentProperties>?> GetAsync(IResourceStore store, string agentKey, CancellationToken ct)
    {
        var env = await store.GetAsync(agentKey, ct);
        return env is null ? null : Map(env);
    }

    /// <summary>Read-modify-write with the fresh ETag; a 412 costs a retry, never a lost write.</summary>
    public static async ValueTask SaveAsync(IResourceStore store, string agentKey, Action<AgentProperties> mutate, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await store.GetAsync(agentKey, ct);
            if (current is null)
                return;
            var mapped = Map(current);
            mutate(mapped.Properties);
            try
            {
                await SaveAsync(store, current, mapped, ct);
                return;
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
        }
    }

    public static async ValueTask SaveAsync(
        IResourceStore store,
        ResourceEnvelope<JsonObject>? existing,
        ResourceEnvelope<AgentProperties> agent,
        CancellationToken ct)
    {
        var json = new ResourceEnvelope<JsonObject>(
            agent.Id,
            agent.Name,
            agent.Type,
            agent.Location,
            agent.Tags,
            JsonSerializer.SerializeToNode(agent.Properties, JsonWire.Options)!.AsObject(),
            agent.ProvisioningState,
            existing?.SystemData ?? agent.SystemData,
            existing?.ETag ?? agent.ETag);
        await store.UpsertAsync(json, existing?.ETag ?? agent.ETag, ct);
    }

    public static ResourceEnvelope<AgentProperties> Map(ResourceEnvelope<JsonObject> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            env.Properties.Deserialize<AgentProperties>(JsonWire.Options) ?? new AgentProperties(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);
}
