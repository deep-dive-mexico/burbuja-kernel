using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Deployments.Abstractions;

namespace Burbuja.Deployments.Targets;

public sealed class LocalProcessTarget(
    IPolicyPipeline<ActionPolicySubject> policy,
    IEventLog events,
    IExecutionGrantAuthority grants,
    IProcessRunner processes,
    IRedactor redactor,
    TimeProvider time) : IDeploymentTarget
{
    public string Kind => "local-process";

    public IReadOnlyList<DeploymentResourceKind> SupportedResourceKinds { get; } = [DeploymentResourceKind.Process];

    public ValueTask<TargetPlan> PlanAsync(TargetPlanRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var changes = request.Desired
            .Where(r => r.Kind == DeploymentResourceKind.Process)
            .Select(r => new PlannedChange(r.Name, r.Kind, StepChange.Create, r.Spec))
            .ToList();
        if (request.Mode == DeploymentMode.Complete)
        {
            // no persisted current; Complete deletes are NoOp
        }

        return new(new TargetPlan(changes));
    }

    public async ValueTask<StepResult> ApplyAsync(StepRequest request, CancellationToken ct)
    {
        var spec = request.Spec;
        var command = spec["command"]?.GetValue<string>() ?? "";
        var cwdRel = spec["cwd"]?.GetValue<string>() ?? ".";
        var workRoot = request.Parameters["workRoot"]?.GetValue<string>() ?? "";
        var cwd = Path.GetFullPath(Path.Combine(workRoot, cwdRel));
        var rootFull = Path.GetFullPath(workRoot);
        if (!cwd.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(cwd, rootFull, StringComparison.Ordinal))
            return new StepResult(StepStatus.Failed, Error: "CwdEscape");

        var profile = PolicyProfileParser.Parse(request.Parameters["profile"]?.GetValue<string>());
        var input = new JsonObject { ["command"] = command, ["cwd"] = cwd };
        var ctx = new PolicyContext(
            request.DeploymentId,
            workRoot,
            workRoot,
            profile,
            "local",
            workRoot,
            [],
            [],
            []);
        var streamId = DeploymentsConstants.StreamId(request.DeploymentId);
        if (!request.ApprovalGranted)
        {
            await events.AppendAsync(new EventAppend(streamId, request.Resource, DeploymentEvents.PolicyIntent, command, input.DeepClone(), RenderLane.Log, request.Resource, null, DeploymentsConstants.StreamType), ct);
            var verdict = policy.Classify(new ActionPolicySubject("shell", input, ctx));
            await events.AppendAsync(new EventAppend(streamId, request.Resource, DeploymentEvents.PolicyDecision, verdict.Tier.ToString(), new JsonObject { ["tier"] = verdict.Tier.ToString(), ["reason"] = verdict.Finding.Reason }, RenderLane.Log, request.Resource, null, DeploymentsConstants.StreamType), ct);
            if (verdict.Tier == RiskTier.HardDeny)
                return new StepResult(StepStatus.Failed, Error: "PolicyDenied: " + verdict.Finding.Reason);
            if (verdict.Tier is RiskTier.Gray or RiskTier.Escalate)
                return new StepResult(StepStatus.AwaitingApproval, NeedsApproval: true, RiskClass: verdict.Finding.Class, Preview: command);
        }

        var args = spec["args"] is JsonArray arr
            ? arr.Select(n => n?.GetValue<string>() ?? "").ToArray()
            : [];
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec["env"] is JsonObject envObj)
        {
            foreach (var kv in envObj)
                env[kv.Key] = kv.Value?.GetValue<string>() ?? "";
        }

        var timeoutSec = spec["timeoutSeconds"]?.GetValue<int>() ?? (int)request.Timeout.TotalSeconds;
        var expect = spec["expectExitCode"]?.GetValue<int>() ?? 0;
        var grant = grants.Mint(request.DeploymentId, "deploy:" + request.Resource, TimeSpan.FromMinutes(10));
        var started = time.GetUtcNow();
        var result = await processes.RunAsync(
            new ProcessStartRequest(command, args, cwd, env, TimeSpan.FromSeconds(Math.Max(1, timeoutSec)), 64 * 1024, true),
            grant,
            ct);
        var duration = (int)(time.GetUtcNow() - started).TotalMilliseconds;
        if (result.TimedOut)
            return new StepResult(StepStatus.Failed, Error: "Timeout");
        if (result.ExitCode != expect)
            return new StepResult(StepStatus.Failed, Error: "ExitCode " + result.ExitCode, Outputs: Outputs(result, duration, redactor));
        return new StepResult(StepStatus.Succeeded, Outputs(result, duration, redactor));
    }

    public ValueTask<StepResult> DestroyAsync(StepRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(new StepResult(StepStatus.Succeeded));
    }

    public ValueTask<TargetProbe> ProbeAsync(string environmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(new TargetProbe(environmentId, true, Kind));
    }

    private static JsonObject Outputs(ProcessResult result, int durationMs, IRedactor redactor) => new()
    {
        ["exitCode"] = result.ExitCode,
        ["stdout"] = redactor.Redact(result.StandardOutput.Length > 65536 ? result.StandardOutput[..65536] : result.StandardOutput),
        ["stderr"] = redactor.Redact(result.StandardError),
        ["durationMs"] = durationMs,
    };
}
