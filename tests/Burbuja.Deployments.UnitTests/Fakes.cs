using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Execution;
using Burbuja.Deployments.Targets;
using Microsoft.Extensions.Time.Testing;

namespace Burbuja.Deployments.UnitTests;

internal sealed class NoopRedactor : IRedactor
{
    public string Redact(string input) => input;
}

internal sealed class ControllableApprovals : IApprovalService
{
    private readonly Dictionary<string, TaskCompletionSource<ApprovalOutcome>> _wait = new(StringComparer.Ordinal);
    public string? LastId { get; private set; }
    public ApprovalRequest? LastRequest { get; private set; }
    public ApprovalOutcome Next = new Approved(false, null, null);
    public bool Hold;

    public ValueTask<string> OpenAsync(ApprovalRequest request, CancellationToken ct)
    {
        LastRequest = request;
        LastId = Guid.NewGuid().ToString("N");
        _wait[LastId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Hold)
            _wait[LastId].TrySetResult(Next);
        return new(LastId);
    }

    public ValueTask<ApprovalOutcome> WaitAsync(string approvalId, CancellationToken ct) =>
        new(_wait[approvalId].Task.WaitAsync(ct));

    public void Release(ApprovalOutcome outcome) => _wait[LastId!].TrySetResult(outcome);

    public ValueTask<ApprovalOutcome> ApproveAsync(string approvalId, string? note, string? grantScope, double? grantHours, CancellationToken ct, string? actorId = null)
    {
        var o = new Approved(false, grantScope, note);
        _wait[approvalId].TrySetResult(o);
        return new(o);
    }

    public ValueTask<ApprovalOutcome> DenyAsync(string approvalId, string? note, CancellationToken ct, string? actorId = null)
    {
        var o = new Denied(note);
        _wait[approvalId].TrySetResult(o);
        return new(o);
    }

    public ValueTask<ApprovalOutcome> AnswerAsync(string approvalId, string answer, CancellationToken ct, string? actorId = null) => new(new Answered(answer));
    public ValueTask<ApprovalOutcome> CancelAsync(string approvalId, CancellationToken ct, string? actorId = null)
    {
        var o = new Cancelled();
        _wait[approvalId].TrySetResult(o);
        return new(o);
    }

    public ValueTask<ApprovalRecord?> GetAsync(string id, CancellationToken ct) => default;
    public ValueTask<PagedResult<ApprovalRecord>> ListAsync(ApprovalQuery query, CancellationToken ct) =>
        new(new PagedResult<ApprovalRecord>([], null));
}

internal sealed class BlockingLeaseStore : ILeaseStore
{
    public ValueTask<LeaseAcquisition?> AcquireAsync(string resource, string owner, TimeSpan ttl, CancellationToken ct) =>
        new(default(LeaseAcquisition?));
    public ValueTask<bool> RenewAsync(string resource, string owner, string fencingToken, TimeSpan ttl, CancellationToken ct) => new(false);
    public ValueTask ReleaseAsync(string resource, string owner, string fencingToken, CancellationToken ct) => default;
    public ValueTask<LeaseAcquisition?> GetAsync(string resource, CancellationToken ct) => default;
    public ValueTask<IReadOnlyList<LeaseAcquisition>> ListExpiredAsync(CancellationToken ct) => new(Array.Empty<LeaseAcquisition>());
}

internal sealed class SpyProcessRunner : IProcessRunner
{
    public int Calls;
    public ProcessStartRequest? Last;

    public ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct)
    {
        Calls++;
        Last = request;
        return new(new ProcessResult(0, "hello\n", "", false));
    }
}

internal sealed class FakePolicy : IPolicyPipeline<ActionPolicySubject>
{
    public RiskTier Tier { get; set; } = RiskTier.Allow;
    public string Reason { get; set; } = "ok";
    public string Fingerprint => "fake";

    public PolicyVerdict Classify(ActionPolicySubject subject) =>
        new(Tier, new PolicyFinding(Tier, "test", "test", Reason), []);
}

internal sealed class RunnerBox
{
    public required DeploymentRunner Runner { get; init; }
    public required DeploymentStateStore States { get; init; }
    public required InMemoryResourceStore Store { get; init; }
    public required InMemoryTarget Target { get; init; }
    public required IEventLog Events { get; init; }
    public required IOperationTracker Ops { get; init; }
    public required CancellationRegistry Cancel { get; init; }
    public required ControllableApprovals Approvals { get; init; }
    public required FakeTimeProvider Time { get; init; }
    public required ILeaseStore Leases { get; init; }
    public required string Key { get; init; }
    public required string Canonical { get; init; }
    public required string EnvironmentId { get; init; }
    public required EnvironmentProperties Environment { get; init; }

    public Task Run(CancellationToken ct) => Runner.RunAsync(Key, EnvironmentId, Environment, ct);

    public static async Task<RunnerBox> CreateAsync(
        ILeaseStore? leases = null,
        ControllableApprovals? approvals = null,
        IEnumerable<DeploymentResource>? resources = null,
        bool requiresApproval = false)
    {
        var time = new FakeTimeProvider();
        var clock = new TimeProviderClock(time);
        var store = new InMemoryResourceStore(clock);
        var states = new DeploymentStateStore(store);
        var target = new InMemoryTarget(time);
        var registry = new TargetRegistry([target]);
        var events = new InMemoryEventLog(new NoopRedactor(), clock);
        var ops = new InMemoryOperationTracker(clock);
        var cancel = new CancellationRegistry();
        approvals ??= new ControllableApprovals();
        leases ??= new InMemoryLeaseStore(clock);
        var runner = new DeploymentRunner(states, registry, leases, approvals, events, ops, cancel, time);
        var environmentId = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Deployments/environments/e";
        var canonical = environmentId + "/deployments/d1";
        var key = canonical.ToLowerInvariant();
        var props = new DeploymentProperties
        {
            Template = new DeploymentTemplate { Resources = [.. resources ?? [new DeploymentResource { Name = "web", Kind = DeploymentResourceKind.Process }]] },
            RequiresApproval = requiresApproval,
            ExecutionState = DeploymentExecutionState.Pending,
            OperationId = "op1",
            TemplateHash = "h",
            RunId = 1,
            Steps = (resources ?? [new DeploymentResource { Name = "web" }]).Select((r, i) => new DeploymentStep
            {
                Resource = r.Name,
                Kind = r.Kind,
                Change = StepChange.Create,
                Wave = r.DependsOn.Count,
                Status = StepStatus.Pending,
            }).ToList(),
        };
        if (resources is null)
            props.Steps = [new DeploymentStep { Resource = "web", Kind = DeploymentResourceKind.Process, Change = StepChange.Create }];
        var env = new ResourceEnvelope<JsonObject>(
            canonical, "d1", "Burbuja.Deployments/environments/deployments", "local",
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToNode(props, JsonWire.Options)!.AsObject(),
            ProvisioningState.Accepted,
            new SystemData(time.GetUtcNow(), "t", "User", time.GetUtcNow(), "t", "User"),
            "");
        await store.UpsertAsync(env, null, CancellationToken.None);
        await ops.StartAsync("op1", "sub1", "Burbuja.Deployments", "local", canonical, null, CancellationToken.None);
        return new RunnerBox
        {
            Runner = runner,
            States = states,
            Store = store,
            Target = target,
            Events = events,
            Ops = ops,
            Cancel = cancel,
            Approvals = approvals,
            Time = time,
            Leases = leases,
            Key = key,
            Canonical = canonical,
            EnvironmentId = environmentId,
            Environment = new EnvironmentProperties(),
        };
    }
}
