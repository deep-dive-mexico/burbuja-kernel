using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Context;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Redaction;

namespace Burbuja.Plugins.Tools.Agents;

internal static class AgentToolPaths
{
    public const string AgentsSegment = "/agents/";

    /// <summary>Accepts a full ARM id or a bare agent name resolved against the caller's scope.</summary>
    public static string? Resolve(string target, ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        if (target.StartsWith('/'))
            return target;
        var stream = context.StreamId;
        var idx = stream.IndexOf("/providers/" + AgentsConstants.Namespace + AgentsSegment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var prefix = stream[idx..];
        var end = prefix.IndexOf("/sessions/", StringComparison.OrdinalIgnoreCase);
        if (end > 0)
            prefix = prefix[..end];
        var agentsPrefix = prefix[..prefix.LastIndexOf('/')] + AgentsSegment;
        return agentsPrefix + target;
    }

    public static JsonObject Schema(params string[] required) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = true,
        ["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
    };
}

/// <summary>Lists adjacent agents without exposing any of their bodies.</summary>
public sealed class ListAgentsTool(IResourceStore store) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "list_agents", "List agents in this scope", AgentToolPaths.Schema(), true, false, true, "agent.read", []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        _ = pass;
        var prefix = context.StreamId;
        var idx = prefix.IndexOf("/agents/", StringComparison.OrdinalIgnoreCase);
        var scope = idx < 0 ? null : prefix[..(idx + "/agents".Length)];
        var page = await store.ListAsync(new ResourceStoreQuery(null, null, null, null, 200, null, scope), ct);
        var teamId = input["teamId"]?.GetValue<string>();
        var items = new JsonArray();
        foreach (var env in page.Value)
        {
            if (!env.Type.EndsWith("/agents", StringComparison.OrdinalIgnoreCase))
                continue;
            if (teamId is not null && env.Properties["teamId"]?.GetValue<string>() != teamId)
                continue;
            items.Add(new JsonObject
            {
                ["id"] = env.Id,
                ["name"] = env.Name,
                ["profile"] = env.Properties["profile"]?.GetValue<string>(),
                ["teamId"] = env.Properties["teamId"]?.GetValue<string>(),
                ["depth"] = env.Properties["depth"]?.GetValue<int>() ?? 0,
            });
        }

        return new ToolResult(new JsonObject { ["agents"] = items }, false);
    }
}

/// <summary>Reads context cards as untrusted background, redacted at read.</summary>
public sealed class ReadAgentContextTool(IResourceStore store, IRedactor redactor) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "read_agent_context", "Read another agent's shared context", AgentToolPaths.Schema("target"), true, false, true, "agent.read", []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        _ = pass;
        var target = AgentToolPaths.Resolve(input["target"]?.GetValue<string>() ?? "", context);
        if (target is null)
            return new ToolResult(JsonValue.Create("the target is not a resolvable agent id")!, true);

        var label = input["label"]?.GetValue<string>();
        var prefix = target.TrimEnd('/') + "/contexts/";
        var page = await store.ListAsync(new ResourceStoreQuery(null, null, null, null, ContextBudget.MaxCards, null, prefix), ct);

        var included = 0;
        var omitted = 0;
        var text = new StringBuilder();
        foreach (var env in page.Value)
        {
            if (label is not null && env.Name != label)
                continue;
            var body = env.Properties["body"]?.GetValue<string>() ?? "";
            if (body.Length == 0)
                continue;
            if (text.Length + body.Length > ContextBudget.MaxCardBytes)
            {
                omitted++;
                continue;
            }

            included++;
            text.AppendLine(UntrustedBackground.Wrap(env.Properties["sourceAgentId"]?.GetValue<string>() ?? env.Name, redactor.Redact(body)));
        }

        var payload = new JsonObject
        {
            ["cards"] = included,
            ["omitted"] = omitted,
            ["text"] = text.ToString(),
        };
        return new ToolResult(payload, false);
    }
}

/// <summary>Searches the target's session transcript, returning redacted snippets.</summary>
public sealed class SessionSearchTool(IResourceStore store, IEventLog events, IRedactor redactor) : IAgentTool
{
    private const int MaxMatches = 20;

    public ToolDescriptor Descriptor { get; } = new(
        "session_search", "Search another agent's session for a phrase", AgentToolPaths.Schema("target", "query"), true, false, true, "agent.read", []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        _ = pass;
        var target = AgentToolPaths.Resolve(input["target"]?.GetValue<string>() ?? "", context);
        var query = input["query"]?.GetValue<string>() ?? "";
        if (target is null || query.Length == 0)
            return new ToolResult(JsonValue.Create("target and query are required")!, true);

        var agent = await store.GetAsync(target.ToLowerInvariant(), ct);
        var sessionId = agent?.Properties["currentSessionId"]?.GetValue<string>();
        if (sessionId is null)
            return new ToolResult(new JsonObject { ["matches"] = new JsonArray() }, false);

        var streamId = AgentsConstants.SessionStreamId($"{target.TrimEnd('/')}/sessions/{sessionId}");
        var matches = new JsonArray();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            await foreach (var evt in events.TailAsync(streamId, 0, cts.Token))
            {
                if (evt.Type != AgentSessionEvents.Message || evt.Payload is not JsonObject payload)
                    continue;
                var text = payload["blocks"]?.ToJsonString() ?? "";
                if (!text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                matches.Add(new JsonObject { ["seq"] = evt.Seq, ["snippet"] = redactor.Redact(text.Length > 400 ? text[..400] : text) });
                if (matches.Count >= MaxMatches)
                    break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        return new ToolResult(new JsonObject { ["matches"] = matches }, false);
    }
}

/// <summary>Posts a bounded, redacted context card for another agent.</summary>
public sealed class PostContextTool(IResourceStore store, IRedactor redactor) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "post_context", "Share a context card with another agent", AgentToolPaths.Schema("target", "label", "body"), false, false, false, "agent.write", []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        _ = pass;
        var target = AgentToolPaths.Resolve(input["target"]?.GetValue<string>() ?? "", context);
        var label = input["label"]?.GetValue<string>() ?? "";
        var body = redactor.Redact(input["body"]?.GetValue<string>() ?? "");
        if (target is null || label.Length == 0)
            return new ToolResult(JsonValue.Create("target and label are required")!, true);

        var bytes = Encoding.UTF8.GetByteCount(body);
        if (bytes > ContextBudget.MaxCardBytes)
            return new ToolResult(JsonValue.Create($"the card is over {ContextBudget.MaxCardBytes} bytes")!, true);

        var id = $"{target.TrimEnd('/')}/contexts/{label}";
        var card = new JsonObject
        {
            ["label"] = label,
            ["kind"] = input["kind"]?.GetValue<string>() ?? "note",
            ["version"] = 1,
            ["bytes"] = bytes,
            ["body"] = body,
            ["sourceAgentId"] = input["from"]?.GetValue<string>(),
            ["sourceSessionId"] = context.StreamId,
            ["visibility"] = input["visibility"]?.GetValue<string>() ?? "parent",
        };
        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                id,
                label,
                $"{AgentsConstants.Namespace}/agents/contexts",
                "local",
                new Dictionary<string, string>(),
                card,
                ProvisioningState.Succeeded,
                new SystemData(DateTimeOffset.UtcNow, "system", "System", DateTimeOffset.UtcNow, "system", "System"),
                ""),
            null,
            ct);

        return new ToolResult(new JsonObject { ["label"] = label, ["bytes"] = bytes }, false);
    }
}

/// <summary>Queues a durable inbox item for another agent.</summary>
public sealed class SendMessageTool(IResourceStore store) : IAgentTool
{
    public const int MaxInboxItems = 64;

    public ToolDescriptor Descriptor { get; } = new(
        "send_message", "Send a message to another agent", AgentToolPaths.Schema("target", "text"), false, false, false, "agent.write", []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        _ = pass;
        var target = AgentToolPaths.Resolve(input["target"]?.GetValue<string>() ?? "", context);
        var text = input["text"]?.GetValue<string>() ?? "";
        if (target is null || text.Length == 0)
            return new ToolResult(JsonValue.Create("target and text are required")!, true);

        var key = target.ToLowerInvariant();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var raw = await store.GetAsync(key, ct);
            if (raw is null)
                return new ToolResult(JsonValue.Create($"no agent '{target}'")!, true);
            var properties = raw.Properties;
            var dedupKey = input["dedupKey"]?.GetValue<string>();
            var delivered = properties["deliveredDedupKeys"] as JsonArray ?? new JsonArray();
            if (dedupKey is not null && delivered.Any(d => d?.GetValue<string>() == dedupKey))
                return new ToolResult(new JsonObject { ["queued"] = false, ["reason"] = "duplicate" }, false);

            var inbox = properties["inbox"] as JsonArray ?? new JsonArray();
            var mode = input["mode"]?.GetValue<string>() ?? "followup";
            inbox.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["source"] = "AgentMessage",
                ["mode"] = char.ToUpperInvariant(mode[0]) + mode[1..],
                ["text"] = text,
                ["dedupKey"] = dedupKey,
                ["enqueuedAt"] = DateTimeOffset.UtcNow,
                ["fromAgentId"] = context.StreamId,
            });
            while (inbox.Count > MaxInboxItems)
                inbox.RemoveAt(0);
            if (dedupKey is not null)
                delivered.Add(dedupKey);
            properties["inbox"] = inbox;
            properties["deliveredDedupKeys"] = delivered;

            try
            {
                await store.UpsertAsync(raw with { ETag = raw.ETag }, raw.ETag, ct);
                var state = ParseState(properties["state"]?.GetValue<string>());
                var action = DeliveryPlan.For(state, mode.ToLowerInvariant() switch
                {
                    "steer" => SendMode.Steer,
                    "inject" => SendMode.Inject,
                    _ => SendMode.Followup,
                });
                return new ToolResult(new JsonObject { ["queued"] = true, ["delivery"] = action.ToString() }, false);
            }
            catch (Exception) when (attempt < 7)
            {
                // A concurrent writer moved the ETag; re-read and try again.
                await Task.Delay(5, ct);
            }
        }

        return new ToolResult(JsonValue.Create("the inbox could not be updated")!, true);
    }

    private static AgentState ParseState(string? raw) =>
        Enum.TryParse<AgentState>(raw, ignoreCase: true, out var state) ? state : AgentState.Idle;
}
