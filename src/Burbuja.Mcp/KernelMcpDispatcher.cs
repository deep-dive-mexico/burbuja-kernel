using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Options;
using Burbuja.Mcp.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Burbuja.Mcp;

/// <summary>
/// Who is calling the kernel over MCP. The agent is named by <c>x-burbuja-agent</c> and resolved to
/// a real resource; the scope is what the authorisation decision is evaluated against.
/// </summary>
public sealed record McpCaller(
    ArmPrincipal Principal,
    string? AgentName,
    string AgentScope,
    string? AgentKey,
    /// <summary>
    /// The MCP SESSION this call arrived on, when the client opened one.
    ///
    /// <para>It is not the agent's session — that is a durable conversation log and outlives any connection.
    /// This is the transport's own identity, minted at <c>initialize</c> and echoed in <c>Mcp-Session-Id</c>.
    /// Before K1 the endpoint was stateless and this was always null, so a call could not be traced to the
    /// connection that made it and a client could not be told its catalogue had changed.</para>
    /// </summary>
    string? SessionId = null);

/// <summary>What a tool call produced, in terms the protocol layer can render without a decision of its own.</summary>
public sealed record McpToolCallOutcome(bool IsError, string Text, bool ApprovalPending = false, string? ApprovalId = null);

/// <summary>A resource the server exposes, already shaped as <c>burbuja://…</c>.</summary>
public sealed record McpResourceDescriptor(string Uri, string Name, string Description, string MimeType);

/// <summary>
/// The kernel's MCP implementation. It owns no protocol type on purpose: the endpoint layer maps
/// MCP requests onto these methods and these results back onto MCP payloads, so the gate behaviour
/// — authorise, ensure a session, decide, execute, answer with the approval id when a human has not
/// decided — is testable without a server.
/// </summary>
public sealed class KernelMcpDispatcher(
    IResourceStore store,
    AgentSessionStore sessions,
    ToolBatchExecutor batch,
    KernelToolExporter exporter,
    IAuthorizationEvaluator authorization,
    IEventLog events,
    IOptions<McpServerOptions> options,
    IConfiguration configuration,
    IRedactor redactor)
{
    private McpServerOptions Options => options.Value;

    // ------------------------------------------------------------------ sessions

    /// <summary>
    /// The agent's CURRENT session, created if this agent has never had one.
    ///
    /// <para>Both the tool path and the session path need it, and they must agree: an MCP connection that
    /// listed tools and then called one has to be ONE agent session, or the audit of a connection is split
    /// across two. That is why this is one helper rather than two copies — the first version of the session
    /// events derived their stream id from the agent key instead, which is a stream nothing writes to, and
    /// the events were invisible to every reader including the fact that was looking for them.</para>
    /// </summary>
    private async ValueTask<(ResourceEnvelope<SessionProperties> Session, bool Created)> EnsureSessionAsync(
        ResourceEnvelope<AgentProperties> agent,
        McpCaller caller,
        CancellationToken ct)
    {
        var (session, created) = await sessions.EnsureAsync(agent, caller.AgentKey!, ct);
        if (created)
        {
            agent.Properties.CurrentSessionId = session.Name;
            await AgentPersistence.SaveAsync(store, caller.AgentKey!, p => p.CurrentSessionId = session.Name, ct);
            await EmitAsync(session.Properties.StreamId, AgentSessionEvents.Started, "started", ct);
        }

        return (session, created);
    }

    /// <summary>
    /// K1: the first request on a connection, recorded on the agent's own stream.
    ///
    /// <para><b>Why the agent's stream and not a new one.</b> A session is a fact about how this agent is
    /// being driven, and the stream is already where its calls are audited; putting the boundary anywhere
    /// else would mean an audit that has to be joined across two streams to answer "what happened in this
    /// connection". The payload names the transport session AND the agent session, because a reader needs
    /// both and the two are not the same thing.</para>
    /// </summary>
    public async ValueTask SessionOpenedAsync(McpCaller caller, DateTimeOffset openedAt, CancellationToken ct)
    {
        if (caller.AgentKey is null || caller.SessionId is not { Length: > 0 } sessionId)
            return;

        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        if (agent is null)
            return;
        var (session, _) = await EnsureSessionAsync(agent, caller, ct);

        await events.AppendAsync(new EventAppend(
            session.Properties.StreamId,
            null,
            "mcp.session.opened",
            $"{caller.Principal.PrincipalId} connected",
            new JsonObject
            {
                ["mcpSessionId"] = sessionId,
                ["principalId"] = caller.Principal.PrincipalId,
                ["agent"] = caller.AgentName,
                ["openedAt"] = openedAt.ToString("O"),
            },
            RenderLane.Log,
            caller.AgentName,
            null,
            AgentsConstants.SessionStreamType), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// K1: the catalogue this connection was shown has moved, and the client has been told.
    ///
    /// <para>Recorded because the notification is not durable: a client that was asleep, or one that
    /// disconnects before reading it, leaves no trace of having been told — and the ledger is where the
    /// question "did anything change under this connection" is answered.</para>
    /// </summary>
    public async ValueTask SessionCatalogueChangedAsync(McpCaller caller, int toolCount, CancellationToken ct)
    {
        if (caller.AgentKey is null || caller.SessionId is not { Length: > 0 } sessionId)
            return;

        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        if (agent is null)
            return;
        var (session, _) = await EnsureSessionAsync(agent, caller, ct);

        await events.AppendAsync(new EventAppend(
            session.Properties.StreamId,
            null,
            "mcp.tools.changed",
            $"{toolCount} tool(s)",
            new JsonObject
            {
                ["mcpSessionId"] = sessionId,
                ["principalId"] = caller.Principal.PrincipalId,
                ["agent"] = caller.AgentName,
                ["tools"] = toolCount,
            },
            RenderLane.Log,
            caller.AgentName,
            null,
            AgentsConstants.SessionStreamType), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ tools

    /// <summary>
    /// The catalogue the caller may see. A caller that names an agent gets that agent's filtered
    /// catalogue, which is the same set the kernel's own activation loop would offer it.
    /// </summary>
    public async ValueTask<IReadOnlyList<McpToolExport>> ListToolsAsync(McpCaller caller, CancellationToken ct)
    {
        if (caller.AgentKey is null)
            return exporter.Export();

        var read = await authorization.AuthorizeAsync(caller.Principal, caller.AgentScope, $"{AgentsConstants.Namespace}/*/read", isDataAction: true, ct);
        if (!read.Allowed)
            return [];

        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        // An unknown agent gets an empty catalogue, never the whole one: the filter is a fact about
        // an agent, and there is no agent here to have one.
        return agent is null ? [] : exporter.ExportFor(agent.Properties);
    }

    /// <summary>
    /// One gated tool call. The identity is authorised first (a read is not enough — this executes),
    /// then the agent must exist, then the same gate and executor the kernel's own loop uses decide
    /// and run it. An escalation is answered immediately with the approval id: an MCP call is an
    /// HTTP request and is never pinned on an approval.
    /// </summary>
    public async ValueTask<McpToolCallOutcome> CallToolAsync(McpCaller caller, string toolName, JsonNode? input, CancellationToken ct)
    {
        if (caller.AgentKey is null)
            return Failure($"'{Abstractions.McpConstants.AgentHeader}' must name the agent this call acts as.");

        var permitted = await authorization.AuthorizeAsync(caller.Principal, caller.AgentScope, AgentActions.Tools, isDataAction: true, ct);
        if (!permitted.Allowed)
            return Failure(permitted.DeniedBecause ?? "not authorized");

        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        if (agent is null)
            return Failure($"unknown agent '{caller.AgentName}'.");

        // Only an agent whose turns happen elsewhere may be driven from here. A kernel-driven
        // agent's tools belong to its activation loop; letting MCP run them too would put two
        // writers on one session log.
        if (!agent.Properties.Driver.Equals(AgentDrivers.External, StringComparison.OrdinalIgnoreCase))
            return Failure($"agent '{caller.AgentName}' is driver '{agent.Properties.Driver}', not 'external'.");

        var known = exporter.ExportFor(agent.Properties).Any(t => t.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (!known)
            return Failure($"unknown tool '{toolName}'.");

        var (session, _) = await EnsureSessionAsync(agent, caller, ct);

        var streamId = session.Properties.StreamId;
        var runId = Guid.NewGuid().ToString("N");
        var arguments = input?.DeepClone() as JsonObject ?? new JsonObject();

        // The caller retries a gated call with the approval id it was handed; the argument is the
        // resume signal ToolBatchExecutor already understands and must never reach the tool itself.
        var resumeApprovalId = arguments["approvalId"]?.GetValue<string>();
        if (resumeApprovalId is { Length: > 0 })
            arguments.Remove("approvalId");

        var call = new ToolUseBlock("mcp_" + Guid.NewGuid().ToString("N")[..12], toolName, arguments);
        var context = AgentToolScope.Context(agent.Properties, caller.AgentKey, streamId, runId, caller.AgentKey, configuration);
        var peers = await AgentAdjacency.PeersAsync(store, caller.AgentKey, agent.Properties.TeamId, ct);
        var subject = new AgentGateSubject(
            Key: caller.AgentKey,
            JobId: EntityIds.Derive(caller.AgentKey).ToString("N"),
            Canonical: agent.Id,
            Language: "en",
            Brief: agent.Properties.Persona ?? agent.Properties.DisplayName,
            Profile: nameof(PermissionProfile.DevFullAuto),
            TargetEnv: TargetEnv.Dev.ToString(),
            VerifyCommands: [],
            Peers: peers,
            IsSubRun: agent.Properties.ParentAgentId is { Length: > 0 },
            CallCount: session.Properties.Turn,
            SpendUsd: agent.Properties.CostUsd,
            WaitFor: TimeSpan.Zero,
            Approvals: batch.Approvals,
            RecordPending: (approvalId, _) =>
            {
                agent.Properties.PendingApprovalId = approvalId;
                return default;
            });

        // The ambient context is what an ambient-aware policy (the supervisor arbiter) and the
        // scripted provider read. An MCP tool call is part of this agent's work, so it sees the
        // same scenario the kernel's own loop would set for it.
        var previous = AgentAmbient.Value;
        AgentAmbient.Value = new AgentAmbient.State(
            JobKey: caller.AgentKey,
            JobId: caller.AgentKey,
            RunId: runId,
            Scenario: agent.Properties.Profile ?? "",
            Role: AgentRunRole.Worker,
            Brief: agent.Properties.Persona ?? "",
            Language: "en",
            Tags: new Dictionary<string, string>(),
            AgentId: caller.AgentKey,
            SessionId: session.Name);
        ToolBatchExecutor.OneResult one;
        try
        {
            one = await batch.ExecuteOne(subject, streamId, runId, call, context, ct, resumeApprovalId);
        }
        finally
        {
            AgentAmbient.Value = previous;
        }

        if (agent.Properties.PendingApprovalId is { Length: > 0 } pending)
            await AgentPersistence.SaveAsync(store, caller.AgentKey, p => p.PendingApprovalId = pending, ct);
        else if (resumeApprovalId is { Length: > 0 })
            await AgentPersistence.SaveAsync(store, caller.AgentKey, p => p.PendingApprovalId = null, ct);

        var block = one.Result
                    ?? (one.Park is { } park ? ToolBatchExecutor.PendingResult(call.Id, park.ApprovalId) : null);
        if (block is null)
            return Failure("the tool produced no result.");

        var text = Render(block.Content);
        var approval = ApprovalIdOf(block.Content);
        return new McpToolCallOutcome(block.IsError, text, approval is not null, approval);
    }

    // ------------------------------------------------------------------ resources

    public async ValueTask<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(McpCaller caller, CancellationToken ct)
    {
        if (caller.AgentKey is null)
            return [];
        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        if (agent is null)
            return [];

        var list = new List<McpResourceDescriptor>
        {
            new($"burbuja://agents/{caller.AgentName}/contexts", "contexts", "Shared context cards this agent can read", "application/json"),
            new($"burbuja://agents/{caller.AgentName}/sessions", "sessions", "This agent's sessions, newest first", "application/json"),
            new($"burbuja://agents/{caller.AgentName}/inbox", "inbox", "Messages waiting for this agent", "application/json"),
        };
        if (agent.Properties.TeamId is { Length: > 0 } team)
            list.Add(new($"burbuja://teams/{Bare(team)}/tasks", "tasks", "The team's task board", "application/json"));
        _ = ct;
        return list;
    }

    /// <summary>
    /// A read-only projection of kernel state, passed through the redactor on the way out. It is a
    /// resource, not a tool: nothing here is gated because nothing here changes anything, and the
    /// caller still had to authenticate and hold a read action on the scope.
    /// </summary>
    public async ValueTask<string?> ReadResourceAsync(McpCaller caller, string uri, CancellationToken ct)
    {
        if (caller.AgentKey is null || !uri.StartsWith("burbuja://", StringComparison.OrdinalIgnoreCase))
            return null;

        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        if (agent is null)
            return null;

        var parts = uri["burbuja://".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null;

        var scope = parts[0].Equals("teams", StringComparison.OrdinalIgnoreCase)
            ? $"{AgentsRoot}/teams/{parts[1]}"
            : caller.AgentScope;
        var read = await authorization.AuthorizeAsync(caller.Principal, scope, $"{AgentsConstants.Namespace}/*/read", isDataAction: true, ct);
        if (!read.Allowed)
            return null;

        return parts[0] switch
        {
            "agents" when parts[2].Equals("contexts", StringComparison.OrdinalIgnoreCase) =>
                await ListAsync($"{caller.AgentKey}/contexts", ct),
            "agents" when parts[2].Equals("sessions", StringComparison.OrdinalIgnoreCase) =>
                await ListAsync($"{caller.AgentKey}/sessions", ct),
            "agents" when parts[2].Equals("inbox", StringComparison.OrdinalIgnoreCase) =>
                Redact(System.Text.Json.JsonSerializer.Serialize(agent.Properties.Inbox)),
            "teams" when parts[2].Equals("tasks", StringComparison.OrdinalIgnoreCase) =>
                await ListAsync($"{AgentsRoot}/teams/{parts[1]}/tasks", ct),
            _ => null,
        };
    }

    /// <summary>The persona, exposed as an MCP prompt so a client can inject it without a tool call.</summary>
    public async ValueTask<string?> GetPersonaAsync(McpCaller caller, CancellationToken ct)
    {
        if (caller.AgentKey is null)
            return null;
        var agent = await AgentPersistence.GetAsync(store, caller.AgentKey, ct);
        return agent?.Properties.Persona is { Length: > 0 } persona ? redactor.Redact(persona) : null;
    }

    // ------------------------------------------------------------------ helpers

    private string AgentsRoot =>
        $"/subscriptions/{Options.SubscriptionId}/resourceGroups/{Options.ResourceGroup}/providers/{AgentsConstants.Namespace}";

    private async ValueTask<string> ListAsync(string prefix, CancellationToken ct)
    {
        var rows = new JsonArray();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await store.ListAsync(
                new ResourceStoreQuery(Options.SubscriptionId, Options.ResourceGroup, AgentsConstants.Namespace, null, 500, token, prefix),
                ct);
            foreach (var v in page.Value)
            {
                rows.Add(new JsonObject
                {
                    ["id"] = v.Id,
                    ["name"] = v.Name,
                    ["properties"] = v.Properties.DeepClone(),
                });
            }

            token = page.NextLink;
            pages++;
        }
        while (!string.IsNullOrEmpty(token) && pages < 40);

        return Redact(rows.ToJsonString());
    }

    private string Redact(string json) => redactor.Redact(json);

    private string Render(JsonNode content)
    {
        var text = content is JsonValue value && value.TryGetValue<string>(out var s) ? s : content.ToJsonString();
        return text.Length <= Options.MaxToolResultChars
            ? text
            : text[..Options.MaxToolResultChars] + $"\n[truncated at {Options.MaxToolResultChars} characters]";
    }

    private static string? ApprovalIdOf(JsonNode content) =>
        content is JsonObject o && o["code"]?.GetValue<string>() == Abstractions.McpConstants.ApprovalPendingCode
            ? o["approvalId"]?.GetValue<string>()
            : null;

    private static string Bare(string value)
    {
        var trimmed = value.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static McpToolCallOutcome Failure(string message) => new(true, message);

    private ValueTask<long> EmitAsync(string streamId, string type, string summary, CancellationToken ct) =>
        events.AppendAsync(new EventAppend(
            streamId,
            null,
            type,
            summary,
            null,
            RenderLane.Output | RenderLane.Log,
            null,
            null,
            AgentsConstants.SessionStreamType), ct);

    internal static string ScopeFor(McpServerOptions options, string agentName) =>
        $"/subscriptions/{options.SubscriptionId}/resourceGroups/{options.ResourceGroup}/providers/{AgentsConstants.Namespace}/agents/{agentName}";
}
