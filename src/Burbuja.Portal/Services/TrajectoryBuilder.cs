using System.Globalization;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Core.Abstractions.Events;

namespace Burbuja.Portal.Services;

/// <summary>The five row families the plan's trajectory ring renders, plus the raw stream's own rows.</summary>
public enum TrajectoryKind
{
    User,
    Assistant,
    Tool,
    Supervisor,
    Approval,
    System,
}

/// <summary>One turn/step-aware row of an execution's ledger.</summary>
public sealed record TrajectoryRow(
    TrajectoryKind Kind,
    long Seq,
    string Type,
    string Title,
    string Text,
    string Status,
    JsonNode? Payload,
    JsonNode? Result,
    DateTimeOffset At,
    int Turn,
    int Step,
    long? DurationMs,
    decimal? Value,
    string? Key = null)
{
    public bool IsTool => Kind == TrajectoryKind.Tool;

    public string KindLabel => Kind.ToString().ToLowerInvariant();
}

/// <summary>
/// Builds the trajectory from the event range — the same source the run itself is derived from, so
/// the portal cannot show a story the kernel did not write. Rows are ordered by sequence; a tool's
/// result is merged into the tool row it answers (matched on the sub-stream id, which is the
/// tool-use id), which is what turns a flat log into steps.
/// </summary>
public static class TrajectoryBuilder
{
    public static IReadOnlyList<TrajectoryRow> Build(IEnumerable<LedgerEvent> events, string? prompt = null)
    {
        var source = events.OrderBy(e => e.Seq).ToList();
        var results = new Dictionary<string, (string Status, JsonNode? Result, LedgerEvent Event)>(StringComparer.Ordinal);
        foreach (var evt in source)
        {
            if (IsToolResult(evt.Type) && evt.SubStreamId is { Length: > 0 } key)
                results[key] = (ToolStatus(evt), evt.Payload, evt);
        }

        var rows = new List<TrajectoryRow>();
        var first = source.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            rows.Add(new TrajectoryRow(
                TrajectoryKind.User,
                0,
                "spec.brief",
                "brief",
                prompt!,
                "sent",
                null,
                null,
                first?.At ?? DateTimeOffset.UnixEpoch,
                0,
                0,
                null,
                null));
        }

        var turn = 0;
        var step = 0;
        var seenTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in source)
        {
            var row = Map(evt, results);
            if (row is null)
                continue;

            // The job path writes both `tool.use` (the model's block) and `tool.intent` (the gate's
            // record) for one tool-use id; the trajectory keeps the first, never two steps for one call.
            if (row.IsTool && evt.SubStreamId is { Length: > 0 } key && !seenTools.Add(key))
                continue;
            if (row.Kind == TrajectoryKind.User)
            {
                turn++;
                step = 0;
            }

            if (row.Kind == TrajectoryKind.Tool)
                step++;
            rows.Add(row with
            {
                Turn = turn,
                Step = row.Kind == TrajectoryKind.Tool ? step : 0,
                Key = evt.SubStreamId,
            });
        }

        return WithDurations(rows);
    }

    /// <summary>Rows the chat ring shows: what a human said, what the model said, what ran, what was asked.</summary>
    public static IReadOnlyList<TrajectoryRow> ChatRows(IReadOnlyList<TrajectoryRow> rows) =>
        [.. rows.Where(r => r.Kind is TrajectoryKind.User or TrajectoryKind.Assistant or TrajectoryKind.Tool or TrajectoryKind.Approval)];

    private static TrajectoryRow? Map(
        LedgerEvent evt,
        IReadOnlyDictionary<string, (string Status, JsonNode? Result, LedgerEvent Event)> results)
    {
        var type = evt.Type;
        switch (type)
        {
            case AgentEventTypes.Init:
                return System(evt, "accepted");
            case AgentEventTypes.AssistantText:
                return new TrajectoryRow(TrajectoryKind.Assistant, evt.Seq, type, "assistant", evt.Summary, "ok", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
            case AgentSessionEvents.Message:
                return Message(evt);
            case AgentEventTypes.ToolIntent:
                return Tool(evt, Text(evt.Payload?["tool"]) ?? evt.Summary, evt.Payload?["input"], results, "running");
            case AgentEventTypes.ToolUse:
                return Tool(evt, evt.Summary, evt.Payload, results, "running");
            case AgentEventTypes.ToolCall:
                return Tool(evt, evt.Summary, evt.Payload, results, "running");
            case AgentEventTypes.ToolResult:
                return results.ContainsKey(evt.SubStreamId ?? "")
                    ? null
                    : Tool(evt, evt.Summary, null, results, ToolStatus(evt));
            case AgentEventTypes.ToolDenied:
                return Tool(evt, evt.Summary, null, results, "denied");
            case AgentEventTypes.Decision:
                return Supervisor(evt, "decision", Text(evt.Payload?["tier"]) ?? evt.Summary);
            case AgentEventTypes.QaLog:
                return Supervisor(evt, "qa", Text(evt.Payload?["verdict"]) ?? evt.Summary);
            case AgentEventTypes.Supervisor:
                return Supervisor(evt, "supervisor", Text(evt.Payload?["decision"]) ?? evt.Summary);
            case AgentEventTypes.Approval:
                return Approval(evt);
            case AgentEventTypes.SteeringReceived:
                return new TrajectoryRow(TrajectoryKind.User, evt.Seq, type, "steering", evt.Summary, "received", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
            case AgentInboxEvents.Enqueued:
            case AgentInboxEvents.Delivered:
                return new TrajectoryRow(TrajectoryKind.User, evt.Seq, type, "inbox", Text(evt.Payload?["text"]) ?? evt.Summary, type.EndsWith("delivered", StringComparison.Ordinal) ? "delivered" : "queued", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
            case AgentEventTypes.Result:
                return System(evt, Text(evt.Payload?["status"]) ?? evt.Summary);
            case AgentEventTypes.Error:
                return new TrajectoryRow(TrajectoryKind.System, evt.Seq, type, "error", evt.Summary, "error", evt.Payload, null, evt.At, 0, 0, null, evt.Value);

            // M9 U3: the turn boundary the telemetry events give the trajectory. Before T1 there was no such
            // event, so a "turn" was inferred from the rows a user message opened — which is why a turn's own
            // cost could not be shown anywhere. `started` is deliberately NOT a row: the completed row IS the
            // turn, and a started-without-completed turn is a run in flight that its assistant row shows.
            case AgentEventTypes.TurnStarted:
                return null;
            case AgentEventTypes.TurnCompleted:
                return new TrajectoryRow(
                    TrajectoryKind.System,
                    evt.Seq,
                    type,
                    Int(evt.Payload?["turn"]) is { } number ? "turn " + number : "turn",
                    TurnText(evt),
                    "ok",
                    evt.Payload,
                    null,
                    evt.At,
                    0,
                    0,
                    Int(evt.Payload?["llmMs"]),
                    evt.Value);

            // ---- Burbuja.Pomnibot jobs (stream type "pomnibot.job") --------------------------
            // The event names differ from the agents catalogue (host.action, tool.decision,
            // mentor.review, …) but the families are the same, so a pomnibot job's trace reads in
            // the same rings: a tool call is a step, a gate verdict is a supervisor row, an
            // approval is an approval card. Without these cases every row would fall into the
            // SYSTEM default and the ring would be a flat log.
            case PomnibotEventTypes.Accepted:
                return System(evt, "accepted");
            case PomnibotEventTypes.Intake:
                return System(evt, "intake");
            case PomnibotEventTypes.ConfirmRequested:
                return System(evt, "awaiting confirmation");
            case PomnibotEventTypes.Started:
                return System(evt, "started");
            case PomnibotEventTypes.Resumed:
                return System(evt, "resumed");
            case PomnibotEventTypes.EngineSpawned:
                return System(evt, "engine spawned");
            case PomnibotEventTypes.AgentLinked:
                return System(evt, "linked to kernel agent");
            case PomnibotEventTypes.HostAction:
                return Tool(evt, "host " + (Text(evt.Payload?["verb"]) ?? evt.Summary), evt.Payload, results, "ok");
            case PomnibotEventTypes.ToolIntent:
            case PomnibotEventTypes.ToolUse:
                return Tool(evt, Text(evt.Payload?["tool"]) ?? evt.Summary, evt.Payload?["input"] ?? evt.Payload, results, "running");
            case PomnibotEventTypes.ToolResult:
                return results.ContainsKey(evt.SubStreamId ?? "")
                    ? null
                    : Tool(evt, evt.Summary, null, results, ToolStatus(evt));
            case PomnibotEventTypes.ToolDenied:
                return Tool(evt, Text(evt.Payload?["tool"]) ?? evt.Summary, null, results, "denied");
            case PomnibotEventTypes.ToolDecision:
                return Supervisor(evt, "decision", Text(evt.Payload?["tier"]) ?? evt.Summary);
            case PomnibotEventTypes.Supervisor:
                return Supervisor(evt, "supervisor", Text(evt.Payload?["decision"]) ?? evt.Summary);
            case PomnibotEventTypes.MentorReview:
                return Supervisor(evt, "mentor", Text(evt.Payload?["verdict"]) ?? evt.Summary);
            case PomnibotEventTypes.MentorScout:
            case PomnibotEventTypes.MentorConsult:
                return Supervisor(evt, "mentor", evt.Summary);
            case PomnibotEventTypes.Qa:
                return Supervisor(evt, "qa", Text(evt.Payload?["verdict"]) ?? evt.Summary);
            case PomnibotEventTypes.ApprovalRequested:
            case PomnibotEventTypes.ApprovalResolved:
            case PomnibotEventTypes.Deferred:
                return Approval(evt);
            case PomnibotEventTypes.Steering:
                return new TrajectoryRow(
                    TrajectoryKind.User, evt.Seq, type, "steering",
                    Text(evt.Payload?["text"]) ?? evt.Summary, "received", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
            case PomnibotEventTypes.AssistantText:
            case PomnibotEventTypes.Report:
                return new TrajectoryRow(
                    TrajectoryKind.Assistant, evt.Seq, type, "assistant",
                    Text(evt.Payload?["text"]) ?? evt.Summary, "ok", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
            case PomnibotEventTypes.Note:
                return new TrajectoryRow(
                    TrajectoryKind.Assistant, evt.Seq, type, "note",
                    Text(evt.Payload?["text"]) ?? evt.Summary, "ok", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
            case PomnibotEventTypes.Progress:
                return System(evt, "progress");
            case PomnibotEventTypes.Verify:
                return System(evt, "verify");
            case PomnibotEventTypes.Delivery:
                return System(evt, "delivery");
            case PomnibotEventTypes.Finalizing:
                return System(evt, "finalizing");
            case PomnibotEventTypes.Compact:
                return System(evt, "compaction");
            case PomnibotEventTypes.StateCard:
                return System(evt, "state card");
            case PomnibotEventTypes.Subagent:
                return System(evt, "subagent");
            case PomnibotEventTypes.BudgetWarned:
                return System(evt, "budget warned");
            case PomnibotEventTypes.BudgetExtended:
                return System(evt, "budget extended");
            case PomnibotEventTypes.Succeeded:
            case PomnibotEventTypes.Failed:
            case PomnibotEventTypes.Cancelled:
                return System(evt, Text(evt.Payload?["executionStatus"]) ?? evt.Summary);
            case PomnibotEventTypes.KernelApprovalOpened:
                return KernelApproval(evt, "pending");
            case PomnibotEventTypes.KernelApprovalApproved:
            case PomnibotEventTypes.KernelApprovalGranted:
                return KernelApproval(evt, "approved");
            case PomnibotEventTypes.KernelApprovalDenied:
                return KernelApproval(evt, "denied");
            case PomnibotEventTypes.KernelApprovalAnswered:
                return KernelApproval(evt, "answered");
            case PomnibotEventTypes.KernelApprovalTimedOut:
                return KernelApproval(evt, "timed out");
            case PomnibotEventTypes.KernelApprovalCancelled:
                return KernelApproval(evt, "cancelled");
            default:
                return System(evt, evt.Summary);
        }
    }

    private static TrajectoryRow Message(LedgerEvent evt)
    {
        var role = Text(evt.Payload?["role"]) ?? evt.Summary;
        var text = MessageText(evt.Payload);
        var kind = role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? TrajectoryKind.Assistant
            : role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? TrajectoryKind.User
                : TrajectoryKind.System;
        return new TrajectoryRow(kind, evt.Seq, evt.Type, role, text, "ok", evt.Payload, null, evt.At, 0, 0, null, evt.Value);
    }

    /// <summary>Concatenates the text blocks of a session message; tool blocks are their own rows.</summary>
    public static string MessageText(JsonNode? payload)
    {
        if (payload?["blocks"] is not JsonArray blocks)
            return "";
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is null || !IsText(block))
                continue;
            var text = Text(block["text"]);
            if (!string.IsNullOrEmpty(text))
                parts.Add(text);
        }

        return string.Join("\n\n", parts);
    }

    private static bool IsText(JsonNode block) =>
        string.Equals(Text(block["type"]), "text", StringComparison.OrdinalIgnoreCase)
        || block["text"] is not null && block["type"] is null;

    private static TrajectoryRow Tool(
        LedgerEvent evt,
        string title,
        JsonNode? payload,
        IReadOnlyDictionary<string, (string Status, JsonNode? Result, LedgerEvent Event)> results,
        string fallbackStatus)
    {
        var status = fallbackStatus;
        JsonNode? result = null;
        if (evt.SubStreamId is { Length: > 0 } key && results.TryGetValue(key, out var found))
        {
            status = found.Status;
            result = found.Result;
        }

        return new TrajectoryRow(
            TrajectoryKind.Tool,
            evt.Seq,
            evt.Type,
            string.IsNullOrEmpty(title) ? "tool" : title,
            evt.Summary,
            status,
            payload,
            result,
            evt.At,
            0,
            0,
            null,
            evt.Value);
    }

    private static TrajectoryRow Supervisor(LedgerEvent evt, string title, string status) =>
        new(TrajectoryKind.Supervisor, evt.Seq, evt.Type, title, evt.Summary, status, evt.Payload, null, evt.At, 0, 0, null, evt.Value);

    private static TrajectoryRow Approval(LedgerEvent evt) =>
        new(
            TrajectoryKind.Approval,
            evt.Seq,
            evt.Type,
            "approval",
            Text(evt.Payload?["approvalId"]) ?? evt.Summary,
            string.IsNullOrEmpty(evt.Summary) ? "pending" : evt.Summary,
            evt.Payload,
            null,
            evt.At,
            0,
            0,
            null,
            evt.Value);

    /// <summary>The kernel's approval lifecycle frame, which rides the same stream as the job.</summary>
    private static TrajectoryRow KernelApproval(LedgerEvent evt, string status) =>
        new(
            TrajectoryKind.Approval,
            evt.Seq,
            evt.Type,
            "approval",
            Text(evt.Payload?["approvalId"]) ?? Text(evt.Payload?["decisionKey"]) ?? evt.Summary,
            status,
            evt.Payload,
            null,
            evt.At,
            0,
            0,
            null,
            evt.Value);

    private static TrajectoryRow System(LedgerEvent evt, string status) =>
        new(TrajectoryKind.System, evt.Seq, evt.Type, evt.Type, evt.Summary, status, evt.Payload, null, evt.At, 0, 0, null, evt.Value);

    private static bool IsToolResult(string type) =>
        type is AgentEventTypes.ToolResult or AgentEventTypes.ToolDenied
            or PomnibotEventTypes.ToolResult;

    /// <summary>A tool's terminal state as the stream spells it: ok, denied, approval_pending or error.</summary>
    public static string ToolStatus(LedgerEvent evt)
    {
        if (evt.Type is AgentEventTypes.ToolDenied or PomnibotEventTypes.ToolDenied)
            return "denied";
        if (string.Equals(evt.Summary, "denied", StringComparison.OrdinalIgnoreCase))
            return "denied";
        if (string.Equals(evt.Summary, "approval_pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Summary, "awaiting_approval", StringComparison.OrdinalIgnoreCase))
            return "awaiting approval";
        if (evt.Payload?["is_error"]?.GetValue<bool>() == true
            || evt.Payload?["error"] is JsonValue error && error.TryGetValue<string>(out var text) && text.Length > 0)
            return "error";
        if (string.Equals(evt.Summary, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Summary, "failed", StringComparison.OrdinalIgnoreCase))
            return "error";
        return "ok";
    }

    /// <summary>
    /// A step's duration is the step's OWN measure when the stream carries one (M9 U3), and only falls back
    /// to the distance to the next row when it does not — that fallback is a property of when rows were
    /// written, not of how long anything took, and it is the reason this used to be the only duration there
    /// was. Nothing is invented: an unpaired running tool has no duration either way.
    /// </summary>
    private static IReadOnlyList<TrajectoryRow> WithDurations(List<TrajectoryRow> rows)
    {
        var result = new List<TrajectoryRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // A tool row's measure rides on its PAIRED RESULT (`agents.tool.result.durationMs`); a turn row's
            // is set by `Map` from the completed event's own `llmMs`.
            long? duration = row.IsTool ? Int(row.Result?["durationMs"]) : row.DurationMs;
            var next = i + 1 < rows.Count ? rows[i + 1] : (TrajectoryRow?)null;
            if (duration is null && row.IsTool && row.Status != "running" && next is not null)
                duration = Math.Max(0, (long)(next.At - row.At).TotalMilliseconds);
            else if (duration is null && !row.IsTool && next is not null && next.Kind != TrajectoryKind.Tool)
                duration = Math.Max(0, (long)(next.At - row.At).TotalMilliseconds);
            result.Add(row with { DurationMs = duration });
        }

        return result;
    }

    /// <summary>
    /// What a completed turn says about itself: the stop reason, how long the provider took, and how many
    /// tokens it produced — the three things a reader wants on a turn header, all of them the event's own.
    /// </summary>
    private static string TurnText(LedgerEvent evt)
    {
        var parts = new List<string>();
        if (Text(evt.Payload?["stopReason"]) is { Length: > 0 } reason)
            parts.Add(reason);
        if (Int(evt.Payload?["llmMs"]) is { } ms)
            parts.Add(ms < 1000 ? $"{ms} ms" : (ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " s");
        if (Int(evt.Payload?["usage"]?["output"]) is { } output)
            parts.Add($"{output:N0} out");
        return parts.Count == 0 ? evt.Summary : string.Join(" · ", parts);
    }

    private static int? Int(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
