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
        return env is null ? null : View(Map(env));
    }

    public async ValueTask<ResourceEnvelope<JobProperties>> PersistAsync(ResourceEnvelope<JobProperties> envelope, CancellationToken ct)
    {
        JobExecutionScope.EnsureOwned(envelope.Id, envelope.Properties);
        return Map(await store.UpsertAsync(ToJson(envelope), envelope.ETag, ct));
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
            var raw = await store.GetAsync(key, ct) ?? throw new InvalidOperationException(key);
            var env = Map(raw);
            JobExecutionScope.EnsureOwned(key, env.Properties);
            var originalState = env.Properties.State;
            var originalStatus = env.Properties.Status;
            var originalApproval = env.Properties.PendingApprovalId;
            var originalError = env.Properties.Error;
            env = View(env);
            mutate(env.Properties);
            if (Checkpoint(env.Properties) is { } run)
            {
                run.State = env.Properties.State;
                run.Status = env.Properties.Status;
                run.PendingApprovalId = env.Properties.PendingApprovalId;
                run.Error = env.Properties.Error;
                if (JobExecutionScope.Current?.OwnsTerminal == false)
                {
                    env.Properties.State = originalState;
                    env.Properties.Status = originalStatus;
                    env.Properties.PendingApprovalId = originalApproval;
                    env.Properties.Error = originalError;
                }
            }
            if (provisioningState is { } state)
                env = env with { ProvisioningState = state };
            try
            {
                return View(Map(await store.UpsertAsync(ToJson(env), env.ETag, ct)));
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412)
            {
                last = ex;
            }
        }

        throw last ?? new ArmException(ArmError.PreconditionFailed("Could not persist job state."));
    }

    private static JobRunCheckpoint? Checkpoint(JobProperties properties) =>
        JobExecutionScope.Current?.RunId is { } id
            ? properties.Recovery?.Runs.FirstOrDefault(run => run.RunId == id)
            : null;

    private static ResourceEnvelope<JobProperties> View(ResourceEnvelope<JobProperties> envelope)
    {
        JobExecutionScope.EnsureOwned(envelope.Id, envelope.Properties);
        if (Checkpoint(envelope.Properties) is { } run && !JobExecution.IsTerminal(envelope.Properties.Status))
        {
            envelope.Properties.State = run.State;
            envelope.Properties.Status = run.Status;
            envelope.Properties.PendingApprovalId = run.PendingApprovalId;
            envelope.Properties.Error = run.Error;
        }
        return envelope;
    }

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
