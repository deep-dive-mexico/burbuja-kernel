using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Runtime;

public sealed class JobStateStore(IResourceStore store)
{
    public async ValueTask<ResourceEnvelope<JobProperties>?> GetAsync(string key, CancellationToken ct)
    {
        var env = await store.GetAsync(key, ct);
        return env is null ? null : Map(env);
    }

    public async ValueTask<ResourceEnvelope<JobProperties>> PersistAsync(ResourceEnvelope<JobProperties> envelope, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var stored = await store.UpsertAsync(ToJson(envelope), envelope.ETag, ct);
                return Map(stored);
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412 && attempt < 7)
            {
                var latest = await store.GetAsync(envelope.Id.ToLowerInvariant(), ct)
                             ?? await store.GetAsync(keyOf(envelope.Id), ct);
                if (latest is null)
                    throw;
                envelope = envelope with { ETag = latest.ETag };
            }
        }

        throw new ArmException(ArmError.PreconditionFailed("Could not persist job state."));
    }

    public async ValueTask<ResourceEnvelope<JobProperties>> MutateAsync(
        string key,
        Action<JobProperties> mutate,
        CancellationToken ct,
        ProvisioningState? provisioningState = null)
    {
        ArmException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var env = await GetAsync(key, ct) ?? throw new InvalidOperationException(key);
            mutate(env.Properties);
            if (provisioningState is { } state)
                env = env with { ProvisioningState = state };
            try
            {
                return await PersistAsync(env, ct);
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412)
            {
                last = ex;
            }
        }

        throw last ?? new ArmException(ArmError.PreconditionFailed("Could not persist job state."));
    }

    private static string keyOf(string id) => id.ToLowerInvariant();

    private static ResourceEnvelope<JsonObject> ToJson(ResourceEnvelope<JobProperties> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            JsonSerializer.SerializeToNode(env.Properties, JsonWire.Options)!.AsObject(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);

    private static ResourceEnvelope<JobProperties> Map(ResourceEnvelope<JsonObject> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            env.Properties.Deserialize<JobProperties>(JsonWire.Options) ?? new JobProperties(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);
}
