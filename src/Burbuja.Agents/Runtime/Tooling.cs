using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Arbiter;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Extensions.Configuration;
using Burbuja.Core.Sdk.Policy;

namespace Burbuja.Agents.Runtime;

public sealed class NarrowingArbiter(
    IGrayZoneArbiter? inner,
    IDecisionCache<ArbiterDecision> cache,
    IEventLog events)
{
    public async ValueTask<ArbiterDecision> DecideAsync(string streamId, ArbiterRequest request, CancellationToken ct)
    {
        var cached = await cache.GetAsync(streamId, DecisionKey(request), ct);
        if (cached is not null)
            return cached;

        ArbiterDecision raw;
        try
        {
            raw = inner is null
                ? new ArbiterDecision(ArbiterVerdict.Escalate, "no arbiter", 0, false, true)
                : await inner.DecideAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            raw = new ArbiterDecision(ArbiterVerdict.Escalate, "malformed", 0, false, false);
        }

        var overridden = ApplyOverrides(raw, request);
        if (!overridden.Overridden)
            await cache.SetAsync(streamId, DecisionKey(request), overridden, ct);

        await events.AppendAsync(new EventAppend(
            streamId,
            request.Subject.ToolName,
            AgentEventTypes.QaLog,
            overridden.Verdict.ToString(),
            new JsonObject
            {
                ["verdict"] = overridden.Verdict.ToString(),
                ["confidence"] = overridden.Confidence,
                ["overridden"] = overridden.Overridden,
            },
            RenderLane.Log,
            request.Subject.ToolName,
            null,
            AgentsConstants.StreamType), ct);
        return overridden;
    }

    private static ArbiterDecision ApplyOverrides(ArbiterDecision raw, ArbiterRequest request)
    {
        if (raw.InjectionSuspected)
            return raw with { Verdict = ArbiterVerdict.Escalate, Overridden = true };
        if (raw.Confidence < 0.6)
            return raw with { Verdict = ArbiterVerdict.Escalate, Overridden = true };
        if (request.CallCount > 40 || request.JobSpendUsd > 3m)
            return raw with { Verdict = ArbiterVerdict.Escalate, Overridden = true, Reason = "over-cap" };
        if (raw.Reason.Contains("malformed", StringComparison.OrdinalIgnoreCase))
            return raw with { Verdict = ArbiterVerdict.Escalate, Overridden = true };
        return raw with { Overridden = false };
    }

    public static string DecisionKey(ArbiterRequest request) =>
        request.Subject.ToolName + ":" + request.ClassifierClass;
}

public sealed class ToolGate(
    IPolicyPipeline<ActionPolicySubject> policy,
    NarrowingArbiter arbiter,
    IApprovalService approvals,
    IEventLog events,
    IRedactor redactor,
    IConfiguration configuration)
{
    public async ValueTask<GateDecision> DecideAsync(GateRequest request, CancellationToken ct)
    {
        var redacted = JsonNode.Parse(redactor.Redact(request.Input.ToJsonString())) ?? new JsonObject();
        await events.AppendAsync(new EventAppend(
            request.StreamId,
            request.ToolUseId,
            AgentEventTypes.ToolIntent,
            request.ToolName,
            new JsonObject
            {
                ["tool"] = request.ToolName,
                ["input"] = redacted.DeepClone(),
                ["hint"] = request.RiskHint,
            },
            RenderLane.Log,
            request.ToolName,
            null,
            AgentsConstants.StreamType), ct);

        PolicyVerdict verdict;
        try
        {
            verdict = policy.Classify(new ActionPolicySubject(request.RiskHint ?? request.ToolName, request.Input, request.Context));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            verdict = new PolicyVerdict(
                RiskTier.HardDeny,
                new PolicyFinding(RiskTier.HardDeny, "policy_error", "policy_error", ex.Message),
                []);
        }

        var decisionSeq = await events.AppendAsync(new EventAppend(
            request.StreamId,
            request.ToolUseId,
            AgentEventTypes.Decision,
            verdict.Tier.ToString(),
            new JsonObject
            {
                ["tier"] = verdict.Tier.ToString(),
                ["class"] = verdict.Finding.Class,
                ["reason"] = redactor.Redact(verdict.Finding.Reason),
                ["fingerprint"] = verdict.Finding.Fingerprint,
            },
            RenderLane.Log,
            request.ToolName,
            null,
            AgentsConstants.StreamType), ct);

        if (verdict.Tier == RiskTier.HardDeny)
            return new GateDecision(GateOutcome.Deny, verdict, null, decisionSeq, DenyText.Format(request.Language, verdict.Finding.Class));

        if (verdict.Tier == RiskTier.Allow)
            return new GateDecision(GateOutcome.Allow, verdict, null, decisionSeq, null);

        if (verdict.Tier == RiskTier.Gray)
        {
            var arb = await arbiter.DecideAsync(request.StreamId, new ArbiterRequest(
                new ToolCallSubject(request.ToolName, request.Input, request.Context.WorkRoot, request.RunId, request.JobId, request.IsSubRun),
                request.Brief,
                verdict.Finding.Class,
                verdict.Tier,
                Preview(request),
                request.CallCount,
                request.JobSpendUsd), ct);
            await events.AppendAsync(new EventAppend(
                request.StreamId,
                request.ToolUseId,
                AgentEventTypes.Supervisor,
                arb.Verdict.ToString(),
                new JsonObject
                {
                    ["decision"] = arb.Verdict.ToString(),
                    ["reason"] = arb.Reason,
                    ["confidence"] = arb.Confidence,
                    ["injection_suspected"] = arb.InjectionSuspected,
                    ["overridden"] = arb.Overridden,
                },
                RenderLane.Log,
                request.ToolName,
                null,
                AgentsConstants.StreamType), ct);
            if (arb.Verdict == ArbiterVerdict.Allow)
                return new GateDecision(GateOutcome.Allow, verdict, null, decisionSeq, null);
            if (arb.Verdict == ArbiterVerdict.Deny)
                return new GateDecision(GateOutcome.Deny, verdict, null, decisionSeq, DenyText.Format(request.Language, verdict.Finding.Class));
        }

        var approvalId = await approvals.OpenAsync(new ApprovalRequest(
            request.StreamId,
            request.ToolUseId,
            ApprovalKind.Permission,
            "tool",
            request.JobCanonical,
            ActionIdentity.Fingerprint(request.ToolName, request.Input, request.Context.WorkRoot),
            verdict.Finding.Class,
            verdict.Finding.Class,
            request.ToolName,
            Preview(request),
            verdict.Finding.Reason,
            ["approve", "deny"],
            true,
            TimeSpan.FromHours(6)), ct);
        await events.AppendAsync(new EventAppend(
            request.StreamId,
            request.ToolUseId,
            AgentEventTypes.Approval,
            "pending",
            new JsonObject { ["approvalId"] = approvalId },
            RenderLane.Log | RenderLane.Monitor,
            request.ToolName,
            null,
            AgentsConstants.StreamType), ct);
        return new GateDecision(GateOutcome.Escalate, verdict, approvalId, decisionSeq, null);
    }

    public TimeSpan DeferAfter =>
        TimeSpan.TryParse(configuration["Policy:DeferAfter"], out var d) ? d : TimeSpan.FromMinutes(10);

    private static string Preview(GateRequest request) => Preview(request.ToolName, request.Input);

    /// <summary>
    /// What a human sees in the approval inbox. The executor compares it when resuming, so a caller
    /// cannot get a benign command approved and then replay the id with a different one.
    /// </summary>
    internal static string Preview(string toolName, JsonNode input) =>
        input["command"]?.GetValue<string>()
        ?? input["path"]?.GetValue<string>()
        ?? toolName;
}

public sealed record GateRequest(
    string StreamId,
    string ToolUseId,
    string ToolName,
    string? RiskHint,
    JsonNode Input,
    PolicyContext Context,
    string Language,
    string Brief,
    string RunId,
    string JobId,
    string JobCanonical,
    bool IsSubRun,
    int CallCount,
    decimal JobSpendUsd);

public enum GateOutcome
{
    Allow,
    Deny,
    Escalate,
}

public sealed record GateDecision(
    GateOutcome Outcome,
    PolicyVerdict Verdict,
    string? ApprovalId,
    long DecisionSeq,
    string? DenyMessage);

public sealed class ToolBatchExecutor(
    IEnumerable<IAgentTool> tools,
    ToolGate gate,
    IApprovalService approvalsField,
    IExecutionGrantAuthority grants,
    IEventLog events,
    ICache cache,
    IRedactor redactor,
    TimeProvider clock,
    JobToolJournal journal)
{
    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Descriptor.Name, t => t, StringComparer.OrdinalIgnoreCase);

    private readonly IApprovalService approvals = approvalsField;

    /// <summary>The approval surface, so a second caller (the MCP server) can build a subject without re-resolving it.</summary>
    public IApprovalService Approvals => approvals;

    public async ValueTask<BatchExecuteResult> ExecuteAsync(
        IGateSubject subject,
        string streamId,
        string runId,
        IReadOnlyList<ToolUseBlock> calls,
        ToolExecutionContext context,
        CancellationToken ct,
        string? resumeApprovalId = null)
    {
        var results = new List<ToolResultBlock>();
        foreach (var call in calls)
        {
            var replayKey = $"tool_exec:{runId}:{call.Id}:{ActionIdentity.Fingerprint(call.Name, call.Input, context.WorkRoot)}";
            var replayed = await cache.GetAsync<ToolResultBlock>(replayKey, ct);
            if (replayed is not null)
            {
                results.Add(replayed);
                continue;
            }

            var one = await ExecuteOne(subject, streamId, runId, call, context, ct, resumeApprovalId);
            if (one.Park is not null)
                return new BatchExecuteResult(results, one.Park);
            if (one.Result is not null)
            {
                await cache.SetAsync(replayKey, one.Result, TimeSpan.FromHours(24), ct);
                results.Add(one.Result);
            }
        }

        return new BatchExecuteResult(results, null);
    }

    public async ValueTask<OneResult> ExecuteOne(
        IGateSubject subject,
        string streamId,
        string runId,
        ToolUseBlock call,
        ToolExecutionContext context,
        CancellationToken ct,
        string? resumeApprovalId = null)
    {
        _tools.TryGetValue(call.Name, out var tool);
        if (resumeApprovalId is { Length: > 0 } rid)
        {
            var rec = await approvals.GetAsync(rid, ct);

            // M5.1 hardening: an approval id is a permission for ONE call, not a token to run
            // anything. Before M5 the id only came from the runtime's own pending batch; over MCP it
            // arrives in the caller's arguments, so it must be matched against the record — same
            // stream, same subject, same tool, and the same preview the human was shown. Anything
            // else falls through to a fresh decision instead of executing.
            var bound = rec is not null
                        && rec.Request.StreamId.Equals(streamId, StringComparison.Ordinal)
                        && rec.Request.SubjectId.Equals(subject.Canonical, StringComparison.OrdinalIgnoreCase)
                         && rec.Request.Action.Equals(call.Name, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(rec.Request.DecisionKey,
                             ActionIdentity.Fingerprint(call.Name, call.Input, context.WorkRoot), StringComparison.Ordinal);

            if (bound && rec!.Outcome is Approved)
            {
                await events.AppendAsync(new EventAppend(
                    streamId,
                    call.Id,
                    AgentEventTypes.Approval,
                    "approved",
                    new JsonObject { ["approvalId"] = rid },
                    RenderLane.Log,
                    call.Name,
                    null,
                    AgentsConstants.StreamType), ct);
                return await RunToolAsync(subject, streamId, runId, call, context, tool, ct);
            }

            if (bound && rec!.Outcome is Denied or Cancelled)
            {
                var text = DenyText.Format(subject.Language, rec.Request.RiskClass);
                return new OneResult(new ToolResultBlock(call.Id, JsonValue.Create(text)!, true), null);
            }
        }
        var hint = tool?.Descriptor.RiskHint ?? call.Name;
        var decision = await gate.DecideAsync(new GateRequest(
            streamId,
            call.Id,
            call.Name,
            hint,
            call.Input,
            new PolicyContext(
                streamId,
                context.WorkRoot,
                context.WorkRoot,
                PolicyProfileParser.Parse(subject.Profile),
                subject.TargetEnv,
                context.WorkRoot,
                subject.Verify,
                [],
                [],
                Peers: subject.Peers),
            subject.Language,
            subject.Brief,
            runId,
            subject.JobId,
            subject.Canonical,
            subject.IsSubRun,
            subject.CallCount,
            subject.SpendUsd), ct);

        if (decision.Outcome == GateOutcome.Deny)
        {
            var text = decision.DenyMessage ?? DenyText.Format(subject.Language, decision.Verdict.Finding.Class);
            await events.AppendAsync(new EventAppend(
                streamId,
                call.Id,
                AgentEventTypes.ToolDenied,
                text,
                new JsonObject { ["class"] = decision.Verdict.Finding.Class, ["text"] = text },
                RenderLane.Output | RenderLane.Log,
                call.Name,
                null,
                AgentsConstants.StreamType), ct);
            var denied = new ToolResultBlock(call.Id, JsonValue.Create(redactor.Redact(text))!, true);
            await events.AppendAsync(new EventAppend(
                streamId,
                call.Id,
                AgentEventTypes.ToolResult,
                "denied",
                new JsonObject { ["is_error"] = true, ["content"] = redactor.Redact(text) },
                RenderLane.Output,
                call.Name,
                null,
                AgentsConstants.StreamType), ct);
            return new OneResult(denied, null);
        }

        if (decision.Outcome == GateOutcome.Escalate && decision.ApprovalId is { } approvalId)
        {
            var outcome = await subject.OnEscalatedAsync(approvalId, ct);
            if (outcome is EscalationOutcome.Pending)
            {
                // M5.1 decision 3: an escalated MCP call is answered, never held. The caller
                // retries with the approval id once a human has decided.
                var pending = PendingResult(call.Id, approvalId);
                await events.AppendAsync(new EventAppend(streamId, call.Id, AgentEventTypes.ToolResult, "approval_pending", pending.Content.DeepClone(), RenderLane.Output, call.Name, null, AgentsConstants.StreamType), ct);
                return new OneResult(pending, null);
            }

            if (outcome is EscalationOutcome.Park)
                return new OneResult(null, new ParkInfo(approvalId, subject.Canonical));
            if (outcome is not EscalationOutcome.Approved)
            {
                var text = DenyText.Format(subject.Language, decision.Verdict.Finding.Class);
                var denied = new ToolResultBlock(call.Id, JsonValue.Create(text)!, true);
                await events.AppendAsync(new EventAppend(streamId, call.Id, AgentEventTypes.ToolDenied, text, null, RenderLane.Log, call.Name, null, AgentsConstants.StreamType), ct);
                await events.AppendAsync(new EventAppend(streamId, call.Id, AgentEventTypes.ToolResult, "denied", new JsonObject { ["is_error"] = true }, RenderLane.Output, call.Name, null, AgentsConstants.StreamType), ct);
                return new OneResult(denied, null);
            }

            await events.AppendAsync(new EventAppend(
                streamId,
                call.Id,
                AgentEventTypes.Approval,
                "approved",
                new JsonObject { ["approvalId"] = approvalId },
                RenderLane.Log,
                call.Name,
                null,
                AgentsConstants.StreamType), ct);
        }

        return await RunToolAsync(subject, streamId, runId, call, context, tool, ct);
    }

    /// <summary>The tool result an escalated call returns when nobody has decided yet.</summary>
    public static ToolResultBlock PendingResult(string toolUseId, string approvalId) =>
        new(
            toolUseId,
            new JsonObject
            {
                ["is_error"] = true,
                ["code"] = "approval_pending",
                ["approvalId"] = approvalId,
            },
            true);

    private async ValueTask<OneResult> RunToolAsync(
        IGateSubject subject,
        string streamId,
        string runId,
        ToolUseBlock call,
        ToolExecutionContext context,
        IAgentTool? tool,
        CancellationToken ct)
    {
        if (tool is null)
        {
            var missing = new ToolResultBlock(call.Id, JsonValue.Create($"unknown tool {call.Name}")!, true);
            return new OneResult(missing, null);
        }

        if (subject is JobGateSubject)
        {
            var previous = await journal.BeginAsync(subject, runId, call, context.WorkRoot, ct);
            if (previous is not null)
                return new OneResult(previous, null);
        }

        var grant = grants.Mint(runId, call.Name + ":" + call.Id, context.Timeout);
        var pass = new GatePass(call.Id, call.Name, call.Name + ":" + call.Id, grant);
        await events.AppendAsync(new EventAppend(
            streamId,
            call.Id,
            AgentEventTypes.ToolCall,
            call.Name,
            new JsonObject { ["tool"] = call.Name },
            RenderLane.Log,
            call.Name,
            null,
            AgentsConstants.StreamType), ct);
        ToolResult executed;
        // M9 T1: how long the TOOL ran, measured around the call itself. A trajectory that draws a tool
        // row has to say whether it took 20 ms or 20 s, and the difference between two row timestamps is
        // not that number.
        var started = clock.GetTimestamp();
        try
        {
            executed = await tool.ExecuteAsync(context, call.Input, pass, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            executed = new ToolResult(JsonValue.Create(redactor.Redact(ex.Message))!, true);
        }

        var durationMs = (int)clock.GetElapsedTime(started).TotalMilliseconds;
        var content = JsonNode.Parse(redactor.Redact(executed.Content.ToJsonString())) ?? executed.Content;
        var result = new ToolResultBlock(call.Id, content, executed.IsError);
        if (subject is JobGateSubject)
            await journal.CompleteAsync(subject, runId, call, result, ct);
        await events.AppendAsync(new EventAppend(
            streamId,
            call.Id,
            AgentEventTypes.ToolResult,
            executed.IsError ? "error" : "ok",
            new JsonObject
            {
                ["is_error"] = executed.IsError,
                ["durationMs"] = durationMs,
                ["content"] = content.DeepClone(),
            },
            RenderLane.Output,
            call.Name,
            null,
            AgentsConstants.StreamType), ct);
        return new OneResult(result, null);
    }

    public sealed record ParkInfo(string ApprovalId, string JobCanonical);
    public sealed record OneResult(ToolResultBlock? Result, ParkInfo? Park);
    public sealed record BatchExecuteResult(IReadOnlyList<ToolResultBlock> Results, ParkInfo? Park);
}
