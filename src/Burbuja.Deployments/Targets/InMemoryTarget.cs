using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Burbuja.Deployments.Abstractions;

namespace Burbuja.Deployments.Targets;

public sealed class InMemoryTarget(TimeProvider time) : IDeploymentTarget
{
    private readonly ConcurrentDictionary<string, JsonObject> _current = new(StringComparer.OrdinalIgnoreCase);

    public string Kind => "in-memory";

    public IReadOnlyList<DeploymentResourceKind> SupportedResourceKinds { get; } = Enum.GetValues<DeploymentResourceKind>();

    public ValueTask<TargetPlan> PlanAsync(TargetPlanRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var desired = request.Desired.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var changes = new List<PlannedChange>();
        foreach (var r in request.Desired.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var key = Key(request.EnvironmentId, r.Name);
            if (!_current.TryGetValue(key, out var existing))
                changes.Add(new PlannedChange(r.Name, r.Kind, StepChange.Create, r.Spec));
            else if (existing.ToJsonString() != r.Spec.ToJsonString())
                changes.Add(new PlannedChange(r.Name, r.Kind, StepChange.Update, r.Spec));
            else
                changes.Add(new PlannedChange(r.Name, r.Kind, StepChange.NoOp, r.Spec));
        }

        if (request.Mode == DeploymentMode.Complete)
        {
            var prefix = request.EnvironmentId.ToLowerInvariant() + "::";
            foreach (var key in _current.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var name = key[prefix.Length..];
                if (!desired.ContainsKey(name))
                    changes.Add(new PlannedChange(name, DeploymentResourceKind.Process, StepChange.Delete, []));
            }
        }

        return new(new TargetPlan(changes));
    }

    public async ValueTask<StepResult> ApplyAsync(StepRequest request, CancellationToken ct)
    {
        if (request.Change == StepChange.Delete)
            return await DestroyAsync(request, ct);
        return await RunKnobs(request, persist: true, ct);
    }

    public async ValueTask<StepResult> DestroyAsync(StepRequest request, CancellationToken ct)
    {
        var result = await RunKnobs(request, persist: false, ct);
        if (result.Status == StepStatus.Succeeded)
            _current.TryRemove(Key(request.EnvironmentId, request.Resource), out _);
        return result.Status == StepStatus.Succeeded
            ? result with { Status = StepStatus.Deleted }
            : result;
    }

    public ValueTask<TargetProbe> ProbeAsync(string environmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(new TargetProbe(environmentId, true, Kind));
    }

    private async ValueTask<StepResult> RunKnobs(StepRequest request, bool persist, CancellationToken ct)
    {
        var spec = request.Spec;
        var test = spec["_test"] as JsonObject;
        var delayMs = test?["delayMs"]?.GetValue<int>() ?? 0;
        if (delayMs > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), time, ct);
        if (test?["hangUntilCanceled"]?.GetValue<bool>() == true)
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var failOn = test?["failOnAttempt"]?.GetValue<int>();
        if (test?["fail"]?.GetValue<bool>() == true || (failOn is int n && n == request.Attempt))
        {
            var code = test?["failWithCode"]?.GetValue<string>() ?? "Failed";
            return new StepResult(StepStatus.Failed, Error: code);
        }

        if (persist)
            _current[Key(request.EnvironmentId, request.Resource)] = (JsonObject)spec.DeepClone();
        var outputs = test?["outputs"] as JsonObject;
        return new StepResult(StepStatus.Succeeded, outputs?.DeepClone().AsObject());
    }

    private static string Key(string environmentId, string name) =>
        environmentId.ToLowerInvariant() + "::" + name;
}
