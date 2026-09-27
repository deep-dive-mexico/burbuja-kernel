using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Targets;

namespace Burbuja.Deployments.Execution;

public sealed class DeploymentRunner(
    DeploymentStateStore states,
    TargetRegistry targets,
    ILeaseStore leases,
    IApprovalService approvals,
    IEventLog events,
    IOperationTracker ops,
    CancellationRegistry cancel,
    TimeProvider time)
{
    public async Task RunAsync(string deploymentKey, string environmentId, EnvironmentProperties environment, CancellationToken ct)
    {
        var lease = await leases.AcquireAsync(DeploymentsConstants.LeaseId(deploymentKey), "runner", TimeSpan.FromSeconds(45), ct);
        if (lease is null)
            return;
        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = RenewLoop(lease, renewCts.Token);
        try
        {
            await ExecuteAsync(deploymentKey, environmentId, environment, ct);
        }
        finally
        {
            await renewCts.CancelAsync();
            try { await renew; } catch (OperationCanceledException) { }
            await leases.ReleaseAsync(lease.Resource, lease.Owner, lease.FencingToken, CancellationToken.None);
            cancel.Forget(deploymentKey);
        }
    }

    private async Task ExecuteAsync(string deploymentKey, string environmentId, EnvironmentProperties environment, CancellationToken ct)
    {
        var env = await states.GetAsync(deploymentKey, ct) ?? throw new InvalidOperationException(deploymentKey);
        if (!targets.TryGet(environment.Target.Kind, out var target))
        {
            await Fail(deploymentKey, "UnsupportedTarget", ct);
            return;
        }

        var runCt = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel.Register(deploymentKey)).Token;
        env = await states.MutateAsync(deploymentKey, p =>
        {
            p.ExecutionState = DeploymentExecutionState.Running;
            p.StartedAt = time.GetUtcNow();
        }, ct, ProvisioningState.Creating);
        await Emit(env, DeploymentEvents.Started, env.Name, ct);
        if (env.Properties.OperationId is { Length: > 0 } opId)
            await ops.ReportAsync(opId, 1, null, ct);

        var byWave = env.Properties.Steps.GroupBy(s => s.Wave).OrderBy(g => g.Key).ToList();
        var outputs = new JsonObject();
        foreach (var wave in byWave)
        {
            env = await states.GetAsync(deploymentKey, ct) ?? env;
            if (env.Properties.ExecutionState == DeploymentExecutionState.Canceling)
            {
                await CancelRemaining(deploymentKey, ct);
                return;
            }

            await Emit(env, DeploymentEvents.WaveStarted, "wave-" + wave.Key, ct);
            var steps = wave.OrderBy(s => s.Resource, StringComparer.Ordinal).ToList();
            if (NeedsApproval(env.Properties, environment, steps, wave.Key))
            {
                if (await Park(deploymentKey, environment, steps, wave.Key, ct) is not null)
                    return;
                env = await states.GetAsync(deploymentKey, ct) ?? env;
            }

            var parallel = Math.Max(1, environment.Defaults.MaxParallel);
            using var gate = new SemaphoreSlim(parallel);
            var tasks = steps.Select(step => RunStepAsync(deploymentKey, environmentId, environment, target, step, outputs, gate, runCt)).ToList();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
                await CancelRemaining(deploymentKey, ct);
                return;
            }

            env = await states.GetAsync(deploymentKey, ct) ?? env;
            if (env.Properties.Steps.Any(s => s.Status == StepStatus.Failed))
            {
                await states.MutateAsync(deploymentKey, p =>
                {
                    foreach (var later in p.Steps.Where(s => s.Status == StepStatus.Pending))
                        later.Status = StepStatus.Skipped;
                }, ct);
                var err = env.Properties.Steps.First(s => s.Status == StepStatus.Failed).Error ?? "Failed";
                await Fail(deploymentKey, err, ct);
                return;
            }

            var actionable = env.Properties.Steps.Count(s => s.Change != StepChange.NoOp);
            var done = env.Properties.Steps.Count(s => s.Status is StepStatus.Succeeded or StepStatus.Deleted or StepStatus.Skipped);
            var percent = actionable == 0 ? 99 : Math.Clamp(100 * done / Math.Max(1, actionable), 1, 99);
            if (env.Properties.OperationId is { Length: > 0 } oid)
                await ops.ReportAsync(oid, percent, null, ct);
        }

        env = await states.MutateAsync(deploymentKey, p =>
        {
            p.ExecutionState = DeploymentExecutionState.Succeeded;
            p.PercentComplete = 100;
            p.EndedAt = time.GetUtcNow();
            p.Outputs = outputs.DeepClone().AsObject();
        }, ct, ProvisioningState.Succeeded);
        if (env.Properties.OperationId is { Length: > 0 } doneOp)
            await ops.SucceedAsync(doneOp, null, ct);
        await Emit(env, DeploymentEvents.Succeeded, env.Name, ct);
    }

    private async Task RunStepAsync(
        string deploymentKey,
        string environmentId,
        EnvironmentProperties environment,
        IDeploymentTarget target,
        DeploymentStep step,
        JsonObject outputs,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var env = await states.GetAsync(deploymentKey, ct) ?? throw new InvalidOperationException(deploymentKey);
            var live = env.Properties.Steps.First(s => s.Resource == step.Resource && s.Wave == step.Wave);
            if (live.Change == StepChange.NoOp)
            {
                env = await states.MutateAsync(deploymentKey, p =>
                {
                    var s = p.Steps.First(x => x.Resource == step.Resource && x.Wave == step.Wave);
                    s.Status = StepStatus.Skipped;
                }, ct);
                await Emit(env, DeploymentEvents.StepSkipped, live.Resource, ct);
                return;
            }

            env = await states.MutateAsync(deploymentKey, p =>
            {
                var s = p.Steps.First(x => x.Resource == step.Resource && x.Wave == step.Wave);
                s.Status = StepStatus.Running;
                s.Attempt++;
                s.StartedAt = time.GetUtcNow();
            }, ct, ProvisioningState.Creating);
            live = env.Properties.Steps.First(s => s.Resource == step.Resource && s.Wave == step.Wave);
            await Emit(env, DeploymentEvents.StepStarted, live.Resource, ct);

            var desired = env.Properties.Template.Resources.FirstOrDefault(r => r.Name == live.Resource);
            var spec = desired?.Spec ?? [];
            var errors = new List<ArmError>();
            JsonObject resolved;
            lock (outputs)
                resolved = ExpressionResolver.Resolve(spec.DeepClone(), env.Properties.Parameters, outputs, "/spec", errors) as JsonObject ?? [];
            if (errors.Count > 0)
            {
                await PersistStep(deploymentKey, step, r =>
                {
                    r.Status = StepStatus.Failed;
                    r.Error = errors[0].Message;
                    r.EndedAt = time.GetUtcNow();
                }, ct);
                await Emit(env, DeploymentEvents.StepFailed, live.Resource, ct);
                return;
            }

            var timeout = environment.Defaults.StepTimeout;
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(timeout);
            var req = new StepRequest(
                environmentId,
                env.Id,
                live.Resource,
                live.Kind,
                live.Change,
                resolved,
                WithEnv(env.Properties.Parameters, environment),
                CloneOutputs(outputs),
                live.Attempt,
                timeout);
            StepResult result;
            try
            {
                result = live.Change == StepChange.Delete
                    ? await target.DestroyAsync(req, stepCts.Token)
                    : await target.ApplyAsync(req, stepCts.Token);
            }
            catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                result = new StepResult(StepStatus.Failed, Error: "Timeout");
            }

            if (result.NeedsApproval)
            {
                await PersistStep(deploymentKey, step, s => s.Status = StepStatus.AwaitingApproval, ct);
                if (await Park(deploymentKey, environment, [step], step.Wave, ct, result) is not null)
                    return;
                req = req with { ApprovalGranted = true };
                result = live.Change == StepChange.Delete
                    ? await target.DestroyAsync(req, stepCts.Token)
                    : await target.ApplyAsync(req, stepCts.Token);
            }

            await PersistStep(deploymentKey, step, s =>
            {
                s.EndedAt = time.GetUtcNow();
                s.Error = result.Error;
                s.Outputs = result.Outputs;
                s.Status = result.Status;
            }, ct);
            if (result.Outputs is not null)
            {
                lock (outputs)
                    outputs[live.Resource] = result.Outputs.DeepClone();
            }

            env = await states.GetAsync(deploymentKey, ct) ?? env;
            await Emit(env, result.Status == StepStatus.Failed ? DeploymentEvents.StepFailed : DeploymentEvents.StepSucceeded, live.Resource, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool NeedsApproval(DeploymentProperties props, EnvironmentProperties environment, List<DeploymentStep> steps, int wave)
    {
        if (props.RequiresApproval && wave == 0)
            return true;
        if (steps.Any(s => props.Template.Resources.Any(r => r.Name == s.Resource && r.RequiresApproval)))
            return true;
        if (environment.Defaults.RequireApprovalForDeletes && steps.Any(s => s.Change == StepChange.Delete))
            return true;
        return false;
    }

    private async Task<ResourceEnvelope<DeploymentProperties>?> Park(
        string deploymentKey,
        EnvironmentProperties environment,
        List<DeploymentStep> steps,
        int wave,
        CancellationToken ct,
        StepResult? fromPolicy = null)
    {
        var env = await states.GetAsync(deploymentKey, ct) ?? throw new InvalidOperationException(deploymentKey);
        var kinds = string.Join(',', steps.Select(s => s.Kind).Distinct().OrderBy(k => k.ToString()));
        var request = new ApprovalRequest(
            DeploymentsConstants.StreamId(env.Id),
            "wave-" + wave,
            ApprovalKind.Confirmation,
            "deployment",
            // M11 S1 — `env.Id` IS the canonical path here (the state store keys deployments by canonical id),
            // and it is what makes the card decidable by whoever holds Burbuja.Deployments/environments/write.
            env.Id,
            $"deploy:{env.Properties.TemplateHash}:wave{wave}",
            $"{environment.Target.Kind}:{kinds}",
            fromPolicy?.RiskClass ?? "deployment",
            "deploy",
            Truncate(fromPolicy?.Preview ?? string.Join(',', steps.Select(s => s.Resource)), 2048),
            "Deployment wave requires approval.",
            ["approve", "deny"],
            true,
            environment.Defaults.ApprovalTimeout);
        env = await states.MutateAsync(deploymentKey, p =>
        {
            p.ExecutionState = DeploymentExecutionState.AwaitingApproval;
            foreach (var s in p.Steps.Where(s => steps.Any(x => x.Resource == s.Resource && x.Wave == s.Wave)))
                s.Status = StepStatus.AwaitingApproval;
        }, ct, ProvisioningState.Updating);
        var id = await approvals.OpenAsync(request, ct);
        env = await states.MutateAsync(deploymentKey, p => p.PendingApprovalId = id, ct);
        await Emit(env, DeploymentEvents.ApprovalRequested, env.Name, ct);
        var outcome = await approvals.WaitAsync(id, ct);
        if (outcome is Deferred)
            outcome = await approvals.WaitAsync(id, ct);
        if (outcome is Deferred)
            outcome = new TimedOut();
        env = await states.MutateAsync(deploymentKey, p => p.PendingApprovalId = null, ct);
        await Emit(env, DeploymentEvents.ApprovalResolved, outcome.GetType().Name, ct);
        switch (outcome)
        {
            case Approved:
                await states.MutateAsync(deploymentKey, p =>
                {
                    p.ExecutionState = DeploymentExecutionState.Running;
                    foreach (var s in p.Steps.Where(s => s.Status == StepStatus.AwaitingApproval))
                        s.Status = StepStatus.Pending;
                }, ct, ProvisioningState.Creating);
                return null;
            case Denied:
                await Fail(deploymentKey, "DeploymentDenied", ct);
                return await states.GetAsync(deploymentKey, ct);
            case TimedOut:
                await Fail(deploymentKey, "ApprovalTimedOut", ct);
                return await states.GetAsync(deploymentKey, ct);
            case Cancelled:
                await CancelRemaining(deploymentKey, ct);
                return await states.GetAsync(deploymentKey, ct);
            default:
                await Fail(deploymentKey, "DeploymentDenied", ct);
                return await states.GetAsync(deploymentKey, ct);
        }
    }

    private async Task Fail(string deploymentKey, string error, CancellationToken ct)
    {
        var env = await states.MutateAsync(deploymentKey, p =>
        {
            p.ExecutionState = DeploymentExecutionState.Failed;
            p.Error = error;
            p.EndedAt = time.GetUtcNow();
        }, ct, ProvisioningState.Failed);
        if (env.Properties.OperationId is { Length: > 0 } op)
            await ops.FailAsync(op, ArmError.Conflict("DeploymentFailed", error), ct);
        await Emit(env, DeploymentEvents.Failed, env.Name, ct);
    }

    private async Task CancelRemaining(string deploymentKey, CancellationToken ct)
    {
        var env = await states.MutateAsync(deploymentKey, p =>
        {
            p.ExecutionState = DeploymentExecutionState.Canceled;
            foreach (var s in p.Steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Running or StepStatus.AwaitingApproval))
                s.Status = StepStatus.Canceled;
            p.EndedAt = time.GetUtcNow();
        }, ct, ProvisioningState.Canceled);
        if (env.Properties.OperationId is { Length: > 0 } op)
            await ops.CancelAsync(op, "canceled", ct);
        await Emit(env, DeploymentEvents.Canceled, env.Name, ct);
    }

    private Task PersistStep(string key, DeploymentStep step, Action<DeploymentStep> mutate, CancellationToken ct) =>
        states.MutateAsync(key, p =>
        {
            var live = p.Steps.First(s => s.Resource == step.Resource && s.Wave == step.Wave);
            mutate(live);
        }, ct).AsTask();

    private async ValueTask Emit(ResourceEnvelope<DeploymentProperties> env, string type, string subject, CancellationToken ct) =>
        await events.AppendAsync(
            new EventAppend(DeploymentsConstants.StreamId(env.Id), subject, type, type, null, RenderLane.Output | RenderLane.Log | RenderLane.Monitor, subject, null, DeploymentsConstants.StreamType),
            ct);

    private async Task RenewLoop(LeaseAcquisition lease, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        while (await timer.WaitForNextTickAsync(ct))
            await leases.RenewAsync(lease.Resource, lease.Owner, lease.FencingToken, TimeSpan.FromSeconds(45), ct);
    }

    private static JsonObject WithEnv(JsonObject parameters, EnvironmentProperties environment)
    {
        var copy = parameters.DeepClone().AsObject();
        copy["workRoot"] = environment.Target.WorkRoot ?? "";
        copy["profile"] = environment.Target.Profile ?? "";
        return copy;
    }

    private static JsonObject CloneOutputs(JsonObject outputs)
    {
        lock (outputs)
            return outputs.DeepClone().AsObject();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
