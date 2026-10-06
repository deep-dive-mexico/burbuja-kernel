using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Sdk.Policy;

namespace Burbuja.Agents.Runtime;

/// <summary>Records an effect's intent before execution and its result before returning; an interrupted intent is never repeated automatically.</summary>
public sealed class JobToolJournal(IResourceStore store, IClock clock)
{
    public static string Key(IGateSubject subject, string runId, ToolUseBlock call) =>
        subject.Key.TrimEnd('/').ToLowerInvariant() + "/runs/" + runId + "/effects/" +
        ActionIdentity.Fingerprint("tool-id", JsonValue.Create(call.Id)!);

    public async ValueTask<ToolResultBlock?> BeginAsync(
        IGateSubject subject, string runId, ToolUseBlock call, string workRoot, CancellationToken ct)
    {
        await JobExecutionScope.EnsureOwnedAsync(store, ct);
        var key = Key(subject, runId, call);
        var fingerprint = ActionIdentity.Fingerprint(call.Name, call.Input, workRoot);
        var previous = await store.GetAsync(key, ct);
        if (previous is not null)
        {
            if (previous.Properties["fingerprint"]?.GetValue<string>() != fingerprint)
                throw new InterruptedToolExecutionException(call.Id, "the executable arguments changed");
            if (previous.Properties["state"]?.GetValue<string>() == "completed")
                return new ToolResultBlock(call.Id, previous.Properties["content"]!.DeepClone(),
                    previous.Properties["isError"]!.GetValue<bool>());
            throw new InterruptedToolExecutionException(call.Id, "the previous execution has no durable result");
        }
        var now = clock.UtcNow;
        try
        {
        await store.CreateAsync(new ResourceEnvelope<JsonObject>(key, call.Id,
            $"{AgentsConstants.Namespace}/workspaces/jobs/runs/effects", "local",
            new Dictionary<string, string>(),
            new JsonObject { ["state"] = "started", ["fingerprint"] = fingerprint, ["tool"] = call.Name },
            ProvisioningState.Updating,
            new SystemData(now, "runtime", "System", now, "runtime", "System"), ""), ct);
        }
        catch (Burbuja.Core.Arm.ArmException ex) when (ex.Error.HttpStatus == 412)
        {
            return await BeginAsync(subject, runId, call, workRoot, ct);
        }
        catch (NotSupportedException)
        {
            throw new InterruptedToolExecutionException(call.Id, "the adapter cannot atomically create execution receipts");
        }
        return null;
    }

    public async ValueTask CompleteAsync(
        IGateSubject subject, string runId, ToolUseBlock call, ToolResultBlock result, CancellationToken ct)
    {
        await JobExecutionScope.EnsureOwnedAsync(store, ct);
        var previous = await store.GetAsync(Key(subject, runId, call), ct)
            ?? throw new InterruptedToolExecutionException(call.Id, "the durable intent disappeared");
        var properties = previous.Properties.DeepClone().AsObject();
        properties["state"] = "completed";
        properties["content"] = result.Content.DeepClone();
        properties["isError"] = result.IsError;
        await store.UpsertAsync(previous with { Properties = properties, ProvisioningState = ProvisioningState.Succeeded }, previous.ETag, ct);
    }
}

public sealed class InterruptedToolExecutionException(string callId, string reason) :
    Exception($"InterruptedToolExecution: '{callId}' requires review because {reason}; the effect was not repeated.");
