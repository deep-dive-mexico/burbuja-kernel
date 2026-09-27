using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Core.Abstractions.Approvals;

namespace Burbuja.Agents.Runtime;

/// <summary>What the gate did with an escalation, from the caller's point of view.</summary>
public enum EscalationOutcome
{
    /// <summary>The approval was granted; run the tool.</summary>
    Approved,

    /// <summary>The approval was refused or cancelled; the tool returns the denial text.</summary>
    Denied,

    /// <summary>Nobody decided in time — the caller stops and resumes from the approval later.</summary>
    Park,

    /// <summary>The approval is recorded and the caller is told its id; nothing blocks.</summary>
    Pending,
}

/// <summary>
/// The facts a gate decision needs, plus the policy for what happens after an escalation.
///
/// M5.1 decision 3: a job and a stateful agent are different subjects with the same gate. The
/// job blocks on <see cref="IApprovalService.WaitAsync"/> and parks on a defer, because a job's
/// whole point is to resume; an agent call arriving over MCP must answer immediately with the
/// approval id, because a held HTTP request is not a protocol. Neither behaviour belongs in the
/// executor, and a non-job key can no longer reach the job state store — that was the M5 hole.
/// </summary>
public interface IGateSubject
{
    /// <summary>The state-store key the escalation is recorded on (job key, agent key).</summary>
    string Key { get; }

    /// <summary>The id the arbiter and the audit trail see: the job id, or a stable derived id for an agent.</summary>
    string JobId { get; }

    string Canonical { get; }

    string Language { get; }

    string Brief { get; }

    /// <summary>The permission profile name (<c>DevFullAuto</c>, <c>ProdReadonly</c>, <c>ReadOnly</c>).</summary>
    string Profile { get; }

    /// <summary>The environment the caller may touch (<c>dev</c>/<c>prod</c>/<c>none</c>).</summary>
    string TargetEnv { get; }

    IReadOnlyList<string> Verify { get; }

    /// <summary>Kernel-supplied adjacency facts; null means "no facts", which escalates.</summary>
    IReadOnlyList<string>? Peers { get; }

    bool IsSubRun { get; }

    int CallCount { get; }

    decimal SpendUsd { get; }

    /// <summary>
    /// Called once per escalation, after the approval exists. Records the pending approval and
    /// decides how long this caller may wait for it.
    /// </summary>
    ValueTask<EscalationOutcome> OnEscalatedAsync(string approvalId, CancellationToken ct);
}

/// <summary>
/// The job subject. Its escalation behaviour is the M4 behaviour, moved here unchanged: the job
/// goes <c>awaitingApproval</c> with the pending id, then blocks on the approval; a defer parks
/// the run so the pending batch replays after the decision.
/// </summary>
public sealed record JobGateSubject(
    string Key,
    Guid JobGuid,
    string Canonical,
    JobSpec Spec,
    string WorkspaceProfile,
    IReadOnlyList<string> VerifyCommands,
    bool IsSubRun,
    int CallCount,
    decimal SpendUsd,
    IReadOnlyList<string>? Peers,
    JobStateStore States,
    IApprovalService Approvals) : IGateSubject
{
    public string JobId => JobGuid.ToString("N");

    public string Language => Spec.Language;

    public string Brief => Spec.Brief;

    public string Profile => WorkspaceProfile;

    public string TargetEnv => Spec.TargetEnv.ToString();

    public IReadOnlyList<string> Verify => VerifyCommands;

    public async ValueTask<EscalationOutcome> OnEscalatedAsync(string approvalId, CancellationToken ct)
    {
        await States.MutateAsync(Key, p =>
        {
            p.Status = JobExecutionStatus.AwaitingApproval;
            p.PendingApprovalId = approvalId;
        }, ct, Burbuja.Core.Abstractions.Arm.ProvisioningState.Updating);

        var outcome = await Approvals.WaitAsync(approvalId, ct);
        return outcome switch
        {
            Approved => EscalationOutcome.Approved,
            Deferred => EscalationOutcome.Park,
            _ => EscalationOutcome.Denied,
        };
    }
}

/// <summary>
/// The stateful-agent subject: same gate, different escalation. The pending id is recorded on the
/// agent resource (which is what makes <c>agents.state</c> read <c>waiting</c>, and what the portal
/// and the MCP caller both read).
///
/// <para><see cref="WaitFor"/> is the whole difference between the two callers. The kernel's own
/// activation loop is bus-driven and has no HTTP request to protect, so it waits — that is the
/// "resume on approval" path. An MCP call is an HTTP request and must never be pinned on
/// <see cref="IApprovalService.WaitAsync"/>, so the MCP server builds the subject with
/// <see cref="TimeSpan.Zero"/> and the caller gets the approval id instead.</para>
/// </summary>
public sealed record AgentGateSubject(
    string Key,
    string JobId,
    string Canonical,
    string Language,
    string Brief,
    string Profile,
    string TargetEnv,
    IReadOnlyList<string> VerifyCommands,
    IReadOnlyList<string>? Peers,
    bool IsSubRun,
    int CallCount,
    decimal SpendUsd,
    TimeSpan WaitFor,
    IApprovalService Approvals,
    Func<string, CancellationToken, ValueTask> RecordPending) : IGateSubject
{
    public IReadOnlyList<string> Verify => VerifyCommands;

    public async ValueTask<EscalationOutcome> OnEscalatedAsync(string approvalId, CancellationToken ct)
    {
        await RecordPending(approvalId, ct);
        if (WaitFor <= TimeSpan.Zero)
            return EscalationOutcome.Pending;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(WaitFor);
        try
        {
            return await Approvals.WaitAsync(approvalId, cts.Token) switch
            {
                Approved => EscalationOutcome.Approved,
                Deferred => EscalationOutcome.Park,
                _ => EscalationOutcome.Denied,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Nobody decided inside the window: the agent is left 'waiting' with the pending id, and
            // the resume action continues it — never a lost tool call.
            return EscalationOutcome.Park;
        }
    }
}
