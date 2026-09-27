using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Context;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Arm;
using Burbuja.Core.Bus;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// The activation worker: one admitted activation per agent, inbox delivery, a model turn, then a
/// settlement notice for the parent. Children settle before their parent (child-first disposal).
///
/// M5.1: a kernel-driven agent now loops on tool use exactly like a job — the same
/// <see cref="ToolGate"/> decides, the same <see cref="ToolBatchExecutor"/> executes, and the cost
/// of every turn is booked from the model the agent actually runs. An agent whose
/// <see cref="AgentProperties.Driver"/> is <c>external</c> is drained and settled but never given a
/// model turn: its turns happen in another process (pomnibot) over MCP.
///
/// M7.0b: activations are dispatched through a <see cref="KeyedWorkDispatcher{T}"/> instead of
/// running inline on the bus's single serial pump. A <c>wake</c> that parks on a human used to hold
/// the whole <c>agents.activation</c> topic for its entire <c>Runtime:AgentApprovalWait</c> (two
/// minutes by default), so every later agent — and, in the M6.0 lane, every later job — sat
/// <c>queued</c> behind one unanswered approval. Per-agent order and the one-activation-per-agent
/// lease are unchanged; only the head-of-line block is gone. <c>interrupt</c> deliberately stays on
/// the pump: cancelling a parked agent must not queue behind the thing it is cancelling.
/// </summary>
public sealed class AgentActivationWorker(
    IMessageBus bus,
    IResourceStore store,
    IEventLog events,
    AgentActivationManager activation,
    AgentSessionStore sessions,
    SessionLog log,
    AgentTurnRunner turns,
    CancellationRegistry cancel,
    IEnumerable<IAgentTool> tools,
    ToolBatchExecutor batch,
    ICostCalculator costs,
    IBudgetLedger ledger,
    ICache cache,
    IConfiguration configuration,
    IApprovalService approvals,
    ILogger<AgentActivationWorker> logger,
    // M14 R12 (request row X1) — an inbox item can carry an image, and turning that into what the MODEL
    // receives needs two things the worker now holds: the artifact store (an image named by key is read here,
    // once) and the provider router (a model that declares no image support is told the truth about it instead
    // of being sent blocks it will reject with a 400).
    IArtifactStore artifacts,
    LlmProviderRouter llmRouter,
    // M10 H3. The day the stop is compared against must be the SAME day the ledger booked against, and the
    // ledger takes its own TimeProvider from DI. On a host with no clock registered both are the system
    // clock; a fact that injects a fake one gets both, instead of a booking on the fake day compared against
    // the real one. Optional so every existing construction site keeps compiling.
    TimeProvider? time = null) : IHostedService
{
    public const string DefaultScenario = "agent-followup-steer-inject";

    /// <summary>M14 R12 (request row X1) — the largest inbound image this kernel will base64 into a transcript.
    /// Over it the message still runs and the model is told the image was not delivered, because a transcript
    /// that silently crested 40 MB would be a store problem discovered at the next read.</summary>
    ///
    /// <para>2026-09-26 (lane CAPS): 5 MB, the per-image bound the Anthropic-shaped wire documents and the AKOS
    /// attachment door enforces — at 4 MB an attachment the door accepted could be refused here, which is two
    /// answers to one question.</para>
    public const long MaxInboundImageBytes = 5 * 1024 * 1024;

    private IAsyncDisposable? _sub;
    private KeyedWorkDispatcher<ActivationWork>? _dispatcher;

    private readonly record struct ActivationWork(string AgentKey, string Op);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var role = configuration["Kernel:Role"] ?? "all";
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;

        _dispatcher = new KeyedWorkDispatcher<ActivationWork>(
            work => work.AgentKey,
            (work, _, ct) => work.Op == "fork"
                ? new ValueTask(ForkAsync(work.AgentKey, null, ct).AsTask())
                : ActivateAsync(work.AgentKey, ct),
            concurrency: ReadInt(configuration, "Runtime:WorkerConcurrency", 4),
            onFault: (ex, key) => logger.LogError(ex, "activation for agent '{AgentKey}' failed", key));

        _sub = await bus.SubscribeAsync(AgentsConstants.TopicActivation, OnMessage, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sub is not null)
            await _sub.DisposeAsync();
        if (_dispatcher is not null)
            await _dispatcher.DisposeAsync(TimeSpan.FromSeconds(20));
    }

    internal ValueTask OnMessage(BusMessage message, CancellationToken ct)
    {
        var payload = message.Payload as JsonObject ?? message.Payload.Deserialize<JsonObject>();
        var key = payload?["agentKey"]?.GetValue<string>();
        var op = payload?["op"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(op))
            return default;

        switch (op.ToLowerInvariant())
        {
            case "wake":
            case "resume":
            case "fork":
                _dispatcher?.TryPost(new ActivationWork(key, op.ToLowerInvariant()));
                break;
            case "interrupt":
                // Inline on purpose: this is the escape hatch for a parked activation, so it must
                // not wait for that activation's lane to drain.
                cancel.Cancel(key);
                break;
        }

        return default;
    }

    /// <summary>Admit, drain the inbox, run the tool loop, settle, notify the parent.</summary>
    public async ValueTask ActivateAsync(string agentKey, CancellationToken ct)
    {
        var raw = await store.GetAsync(agentKey, ct);
        if (raw is null)
            return;
        var agent = AgentPersistence.Map(raw);

        var lease = await activation.AdmitAsync(agentKey, Environment.MachineName, TimeSpan.FromMinutes(5), ct);
        if (lease is null)
            return; // another activation holds the fenced lease

        try
        {
            // M10 H3: the day's stop, FIRST — before a session is opened or an inbox is drained.
            //
            // An agent has no USD cap of its own (the stateful path never had one), so the daily stop is the
            // ONLY ceiling a persona agent — and therefore every AKOS turn — can meet. Two placement details
            // are load-bearing. It runs before the first model turn, because refusing inside `BookAsync` would
            // refuse AFTER the turn was already paid for: that hides the spend instead of stopping it. And it
            // runs before `EnsureSessionAsync`, so a parked activation leaves the agent exactly as it was —
            // no session id on an agent that never ran a turn, nothing to explain later. The event goes on the
            // session stream the agent WOULD have used, so the refusal is where a reader looks for the work.
            if (await DailyHardStop.ReadAsync(configuration, ledger, time ?? TimeProvider.System, ct).ConfigureAwait(false) is { } hardStop)
            {
                await events.AppendAsync(new EventAppend(
                    AgentsConstants.SessionStreamId(raw.Id), null, HardStopReading.EventType, "hardstop",
                    hardStop.ToJson(), RenderLane.Log | RenderLane.Output, agent.Name, null,
                    AgentsConstants.SessionStreamType), ct);
                logger.LogWarning("agent {Agent} parked at the day's hard stop: {Reason}", agentKey, hardStop.Sentence);
                return;
            }

            var session = await EnsureSessionAsync(agentKey, agent, ct);
            var streamId = session.Properties.StreamId;

            // The drain's sequence comes BACK: the bound the transcript is derived to has to include what the
            // drain just appended, or the turn is asked about a conversation that stops before the message.
            var lastSeq = await DrainInboxAsync(agent, streamId, session.Properties.LastSeq, ct);
            await PersistAsync(agentKey, p =>
            {
                p.Inbox = agent.Properties.Inbox;
                p.CurrentSessionId = agent.Properties.CurrentSessionId;
            }, ct);

            var scenario = string.IsNullOrWhiteSpace(agent.Properties.Profile) ? DefaultScenario : agent.Properties.Profile!;
            var lastTurn = AgentTurnResult.Empty;

            if (IsExternal(agent.Properties))
            {
                // An external agent's turns belong to another process. Draining the inbox into the
                // session log is the whole job here, so a message sent to it is not lost and the
                // portal can see it.
                await events.AppendAsync(new EventAppend(streamId, null, AgentActivationEvents.Settled, "external", null, RenderLane.Output | RenderLane.Log, agent.Name, null, AgentsConstants.SessionStreamType), ct);
                await NotifyParentAsync(agentKey, agent, lastTurn, ct);
                return;
            }

            var runId = Guid.NewGuid().ToString("N");
            var specs = ToolSpecs(agent.Properties);
            var peers = await AgentAdjacency.PeersAsync(store, agentKey, agent.Properties.TeamId, ct);
            var execCtx = AgentToolScope.Context(agent.Properties, agentKey, streamId, runId, agentKey, configuration);
            var maxSteps = ReadInt(configuration, "Runtime:AgentToolSteps", 8);
            var waiting = false;

            // M9 T1: how many MODEL turns this activation actually ran. The session's `turn` counter used
            // to be incremented once per activation, so an activation whose loop ran three turns reported
            // one — the undercount `07-testing.md` names, and the number the arbiter reads.
            var turnsRun = 0;

            for (var step = 0; step < maxSteps; step++)
            {
                if (cancel.IsCanceled(agentKey))
                    break;

                await EmitTurnStartedAsync(streamId, runId, step, agent, ct);
                lastTurn = await turns.RunTurnAsync(streamId, agent.Properties, lastSeq, scenario, runId, specs, ct);
                lastSeq = Math.Max(lastSeq, lastTurn.Seq);
                turnsRun++;
                var bookingId = await BookAsync(agentKey, agent, session, runId, step, lastTurn, ct);
                await EmitTurnCompletedAsync(streamId, runId, step, agent, lastTurn, bookingId, ct);
                await PersistAsync(agentKey, p => p.CostUsd = agent.Properties.CostUsd, ct);

                if (lastTurn.StopReason != LlmStopReason.ToolUse)
                    break;

                var calls = lastTurn.Blocks.OfType<ToolUseBlock>().ToList();
                if (calls.Count == 0)
                    break;

                var subject = new AgentGateSubject(
                    Key: agentKey,
                    JobId: EntityIds.Derive(agentKey).ToString("N"),
                    Canonical: raw.Id,
                    Language: "en",
                    Brief: agent.Properties.Persona ?? agent.Properties.DisplayName,
                    // An agent has no workspace profile binding: its gate profile is the full-auto
                    // development profile, and an agent profile name is the activation scenario.
                    Profile: nameof(PermissionProfile.DevFullAuto),
                    TargetEnv: TargetEnv.Dev.ToString(),
                    VerifyCommands: [],
                    Peers: peers,
                    IsSubRun: agent.Properties.ParentAgentId is { Length: > 0 },
                    CallCount: step,
                    SpendUsd: agent.Properties.CostUsd,
                    WaitFor: ReadApprovalWait(configuration),
                    Approvals: approvals,
                    RecordPending: (approvalId, _) =>
                    {
                        agent.Properties.PendingApprovalId = approvalId;
                        return default;
                    });

                // One user message per assistant turn, exactly like the job path: the transcript a
                // real provider sees must not depend on which lane produced it.
                var results = new List<ContentBlock>();
                for (var i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    var one = await batch.ExecuteOne(subject, streamId, runId, call, execCtx, ct);
                    var block = one.Result
                                ?? (one.Park is { } park ? ToolBatchExecutor.PendingResult(call.Id, park.ApprovalId) : null);
                    if (block is not null)
                        results.Add(block);
                    if (one.Park is null)
                        continue;

                    // Every tool_use id in this assistant message needs a tool_result or the next
                    // request is malformed: the calls after the parked one are answered as pending
                    // too, and the model retries them once a human has decided.
                    waiting = true;
                    for (var j = i + 1; j < calls.Count; j++)
                        results.Add(ToolBatchExecutor.PendingResult(calls[j].Id, one.Park.ApprovalId));
                    break;
                }

                if (results.Count > 0)
                    lastSeq = Math.Max(lastSeq, await log.AppendMessageAsync(streamId, null, new InputMessage("user", results), ct));

                await PersistAsync(agentKey, p =>
                {
                    p.CostUsd = agent.Properties.CostUsd;
                    p.PendingApprovalId = agent.Properties.PendingApprovalId;
                }, ct);

                if (waiting)
                    break;
            }

            await sessions.UpdateAsync(session.Id.ToLowerInvariant(), p =>
            {
                p.LastSeq = Math.Max(p.LastSeq, lastSeq);
                p.Turn += turnsRun;
                p.Status = waiting ? "waiting" : "active";
                p.CostUsd = (p.CostUsd == 0 ? agent.Properties.CostUsd : p.CostUsd);
            }, ct);
            await EmitAsync(streamId, AgentActivationEvents.Settled, waiting ? "waitingApproval" : "settled", ct);
            await NotifyParentAsync(agentKey, agent, lastTurn, ct);
        }
        finally
        {
            await activation.ReleaseAsync(agentKey, lease.Owner, lease.FencingToken, ct);
        }
    }

    private static bool IsExternal(AgentProperties agent) =>
        agent.Driver.Equals(AgentDrivers.External, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tool catalogue the model sees: the agent's filter when it has one, everything otherwise.
    /// A filter that names no registered tool yields no tools — never a silent "all tools".
    /// </summary>
    private IReadOnlyList<ToolSpec> ToolSpecs(AgentProperties agent)
    {
        var filter = agent.ToolFilter;
        var selected = filter.Count == 0
            ? tools
            : tools.Where(t => filter.Contains(t.Descriptor.Name, StringComparer.OrdinalIgnoreCase));
        return selected
            .Select(t => new ToolSpec(t.Descriptor.Name, t.Descriptor.Description, t.Descriptor.InputSchema))
            .ToArray();
    }

    /// <summary>
    /// Books one turn's usage. Returns the ledger entry's id (M9 T1) so the turn's `completed` event can
    /// cite the ONE record of what the turn cost; <see cref="Guid.Empty"/> when nothing was booked, which
    /// is the unpriced-model path and is named on the event rather than left to look like a zero charge.
    /// </summary>
    private async ValueTask<Guid> BookAsync(
        string agentKey,
        ResourceEnvelope<AgentProperties> agent,
        ResourceEnvelope<SessionProperties> session,
        string runId,
        int step,
        AgentTurnResult turn,
        CancellationToken ct)
    {
        var bookKey = $"booked:{agentKey}:{runId}:{step}:agent";
        if (!await cache.TryAddAsync(bookKey, true, TimeSpan.FromHours(24), ct))
            return Guid.Empty;

        // An agent's default model is deepseek-flash, which a host that never staged the DeepSeek
        // pricebook cannot price. That is unknown cost, not zero cost: it is recorded as a note and
        // nothing is booked, instead of inventing a number or failing the turn.
        if (costs.TryGet(agent.Properties.ModelId) is null)
        {
            await events.AppendAsync(new EventAppend(
                session.Properties.StreamId,
                runId,
                AgentEventTypes.Note,
                "unpriced model: " + agent.Properties.ModelId,
                new JsonObject { ["modelId"] = agent.Properties.ModelId, ["booked"] = false },
                RenderLane.Log,
                agent.Name,
                null,
                AgentsConstants.SessionStreamType), ct);
            return Guid.Empty;
        }

        var usd = costs.Quote(agent.Properties.ModelId, turn.Usage);
        var bookingId = await ledger.BookAsync(new BudgetBooking(
            EntityIds.Derive(agentKey),
            EntityIds.Derive(runId),
            BudgetKind.Worker,
            usd,
            turn.Usage.InputTokens,
            turn.Usage.OutputTokens), ct);

        agent.Properties.CostUsd += usd;
        await sessions.UpdateAsync(session.Id.ToLowerInvariant(), p => p.CostUsd += usd, ct);
        return bookingId;
    }

    private static TimeSpan ReadApprovalWait(IConfiguration configuration) =>
        TimeSpan.TryParse(configuration["Runtime:AgentApprovalWait"], out var wait) ? wait : TimeSpan.FromMinutes(2);

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var n) && n > 0 ? n : fallback;

    /// <summary>Fork seeds a new session at the source session's end-seed boundary.</summary>
    public async ValueTask<string?> ForkAsync(string agentKey, string? newSessionId, CancellationToken ct)
    {
        var raw = await store.GetAsync(agentKey, ct);
        if (raw is null)
            return null;
        var agent = AgentPersistence.Map(raw);
        var sourceKey = agent.Properties.CurrentSessionId is { Length: > 0 } id
            ? AgentSessionStore.SessionKey(raw.Id, id)
            : null;
        var source = sourceKey is null ? null : await sessions.GetAsync(sourceKey, ct);
        var sessionId = newSessionId ?? "session-" + Guid.NewGuid().ToString("N")[..12];
        var created = await sessions.CreateAsync(
            raw.Id,
            raw.Name,
            sessionId,
            source?.Properties.StreamId,
            source?.Properties.LastSeq,
            source?.Id,
            agent.Properties.TeamId,
            ct);

        agent.Properties.CurrentSessionId = sessionId;
        await PersistAgentAsync(raw, agent, ct);
        await EmitAsync(created.Properties.StreamId, AgentSessionEvents.Forked, "forked", ct);
        return sessionId;
    }

    private async ValueTask<ResourceEnvelope<SessionProperties>> EnsureSessionAsync(
        string agentKey,
        ResourceEnvelope<AgentProperties> agent,
        CancellationToken ct)
    {
        // The create-if-absent rule lives in AgentSessionStore.EnsureAsync, shared with the MCP
        // server; this only announces a session that did not exist a moment ago.
        var (session, created) = await sessions.EnsureAsync(agent, agentKey, ct);
        // The caller persists the agent once, after the inbox is drained, so the ETag stays valid.
        agent.Properties.CurrentSessionId = session.Name;
        if (created)
            await EmitAsync(session.Properties.StreamId, AgentSessionEvents.Started, "started", ct);
        return session;
    }

    /// <summary>
    /// Drains the inbox into the session log and RETURNS the highest sequence it appended.
    ///
    /// <para><b>M10 A11 — the discarded sequence, and what it cost.</b> This returned nothing and threw the
    /// appended sequence away with `_ = seq`, while the caller had already read `lastSeq` from the session
    /// BEFORE the drain. The turn's history is derived up to that bound (`AgentTurnRunner` calls
    /// `SessionLog.DeriveAsync(streamId, upToSeq)`), so the message the inbox had just delivered sat ABOVE the
    /// bound and was never in the transcript: the persona's model call went out with an EMPTY message list and
    /// the provider answered `400 — "messages: at least one message is required"`. Measured on the release host
    /// 2026-09-14: a persona settled with `cost 0`, its inbox drained, and its answer `(sin respuesta)`, which
    /// is exactly what a turn looks like when nobody asked the model anything.</para>
    ///
    /// <para>The scripted fixture cannot see this — it matches its transcript by turn index and ignores the
    /// messages — so no lane caught it and only a real provider could.</para>
    /// </summary>
    private async ValueTask<long> DrainInboxAsync(ResourceEnvelope<AgentProperties> agent, string streamId, long upToSeq, CancellationToken ct)
    {
        if (agent.Properties.Inbox.Count == 0)
            return upToSeq;

        foreach (var item in agent.Properties.Inbox.ToList())
        {
            var body = item.Source == InboxSourceKind.HumanSend
                ? item.Text
                : UntrustedBackground.Wrap(item.FromAgentId ?? item.Source.ToString(), item.Text);

            // M14 R12 (request row X1) — the image, as the model can actually receive it. The transcript
            // records what HAPPENED, so a message whose image could not be delivered says so in the same
            // message rather than arriving as text with a silent hole in it.
            var content = new List<ContentBlock> { new TextBlock(body) };
            if (item.Image is { } image)
                content.Add(await ResolveImageAsync(agent, image, ct));
            foreach (var more in item.MoreImages ?? [])
                content.Add(await ResolveImageAsync(agent, more, ct));

            var seq = await log.AppendMessageAsync(streamId, null, new InputMessage("user", content), ct);
            await EmitAsync(streamId, AgentInboxEvents.Delivered, item.Mode.ToString(), ct, item.Id);
            agent.Properties.Inbox.Remove(item);
            upToSeq = Math.Max(upToSeq, seq);
        }

        return upToSeq;
    }

    /// <summary>
    /// M14 R12 (request row X1) — one inbox image as a content block. Two honest refusals become the model's
    /// own sentence instead of a dropped attachment: a model that declares no image support, and an artifact
    /// that was there when the message was accepted and is gone now. Neither is a failure of the turn — the
    /// person's text still runs — and neither is silent, which is the whole rule this kernel applies to an
    /// absent value.
    /// </summary>
    private async ValueTask<ContentBlock> ResolveImageAsync(
        ResourceEnvelope<AgentProperties> agent,
        AgentImage image,
        CancellationToken ct)
    {
        var modelId = agent.Properties.ModelId;
        if (!llmRouter.Provider.Capabilities(modelId).SupportsImages)
        {
            return new TextBlock(
                $"(an image was attached to this message and was NOT delivered: model '{modelId}' declares no "
                + "image support. The person's text is below; answer from it and say you cannot see the image.)");
        }

        if (image.HasUrl)
            return new ImageBlock(new ImageSource("url", image.MediaType, null, image.Url));

        var blob = await artifacts.GetAsync(image.ArtifactKey!, ct);
        if (blob is null)
        {
            return new TextBlock(
                $"(an image was attached to this message as artifact '{image.ArtifactKey}' and could NOT be read: "
                + "it is no longer in the artifact store. Say so rather than describing an image you did not get.)");
        }

        await using var content = blob.Content;
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxInboundImageBytes)
        {
            return new TextBlock(
                $"(an image was attached to this message and was NOT delivered: {buffer.Length} bytes is over the "
                + $"{MaxInboundImageBytes}-byte bound for one inbound image. Say so rather than guessing at it.)");
        }

        // 2026-09-26 (live review) — an artifact store may hand the bytes back as `application/octet-stream`, and the
        // provider answers such a block with a 400 (ÓPTICA's first live image turn died there). The media type is
        // the store's only when it IS an image type, else the message's, else what the bytes themselves say.
        var bytes = buffer.ToArray();
        var mediaType = IsImageType(blob.ContentType) ? blob.ContentType!
            : IsImageType(image.MediaType) ? image.MediaType
            : SniffImageType(bytes) ?? image.MediaType;
        // 2026-09-26 (live review) — the session log records the image by REFERENCE (`artifact` + key), never as
        // base64: the event log redacts and spills every string it stores, and a 1.7 MB base64 string came back
        // altered ("base64 decode error" from DeepSeek on ÓPTICA's first live image turn). AgentTurnRunner turns
        // the reference into bytes when it builds the request, so the model still receives the image itself.
        return new ImageBlock(new ImageSource(AgentTurnRunner.ArtifactImageKind, mediaType, null, image.ArtifactKey));
    }

    private static bool IsImageType(string? mediaType) =>
        mediaType is { Length: > 6 } && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    internal static string? SniffImageType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38)
            return "image/gif";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
            return "image/webp";
        return null;
    }

    private async ValueTask NotifyParentAsync(
        string agentKey,
        ResourceEnvelope<AgentProperties> agent,
        AgentTurnResult turn,
        CancellationToken ct)
    {
        if (agent.Properties.ParentAgentId is not { Length: > 0 } parentId)
            return;

        var parentKey = parentId.ToLowerInvariant();
        var result = new SubagentResult(
            Output: "settled",
            Structured: null,
            Diagnostic: null,
            StopReason: SubagentStopReason.Completed);

        // Exactly one notice per member, ever: a re-woken member settles again, and sibling
        // activations settle concurrently. The append is an ETag-guarded read-modify-write, so a
        // concurrent writer costs a retry rather than a lost or duplicated notice.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var parentRaw = await store.GetAsync(parentKey, ct);
            if (parentRaw is null)
                return;

            var parent = AgentPersistence.Map(parentRaw);
            var notice = activation.BuildSettlementNotice(agent.Name, result);
            var alreadyNotified = parent.Properties.Inbox.Any(i =>
                                     string.Equals(i.DedupKey, notice.DedupKey, StringComparison.Ordinal))
                                 || (notice.DedupKey is { Length: > 0 } key && parent.Properties.DeliveredDedupKeys.Contains(key));
            if (alreadyNotified)
                return;

            parent.Properties.Inbox.Add(notice);
            try
            {
                await PersistAgentAsync(parentRaw, parent, ct);
                break;
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
        }

        var streamId = agent.Properties.CurrentSessionId is { Length: > 0 } s
            ? AgentsConstants.SessionStreamId($"{agent.Id}/sessions/{s}")
            : AgentsConstants.SessionStreamId(agentKey);
        await EmitAsync(streamId, AgentSubagentEvents.Settled, "settled", ct);
        _ = turn;
    }

    private ValueTask PersistAsync(string agentKey, Action<AgentProperties> mutate, CancellationToken ct) =>
        AgentPersistence.SaveAsync(store, agentKey, mutate, ct);

    private ValueTask PersistAgentAsync(ResourceEnvelope<JsonObject>? existing, ResourceEnvelope<AgentProperties> agent, CancellationToken ct) =>
        AgentPersistence.SaveAsync(store, existing, agent, ct);

    private ValueTask<long> EmitAsync(string streamId, string type, string summary, CancellationToken ct, string? subject = null) =>
        events.AppendAsync(new EventAppend(
            streamId,
            subject,
            type,
            summary,
            null,
            RenderLane.Output | RenderLane.Log,
            subject,
            null,
            AgentsConstants.SessionStreamType), ct);

    /// <summary>
    /// M9 T1: opens a turn on the session stream. Its only job is to give the trajectory a row that says
    /// "the model call started here" — the timing lives on the matching <c>completed</c> event, which is
    /// where the provider call has actually returned.
    /// </summary>
    private ValueTask<long> EmitTurnStartedAsync(
        string streamId, string runId, int step, ResourceEnvelope<AgentProperties> agent, CancellationToken ct) =>
        events.AppendAsync(new EventAppend(
            streamId,
            runId,
            AgentEventTypes.TurnStarted,
            $"turn {step}",
            new JsonObject
            {
                ["turn"] = step,
                ["runId"] = runId,
                ["model"] = agent.Properties.ModelId,
                ["effort"] = agent.Properties.Effort,
            },
            RenderLane.Log,
            agent.Name,
            null,
            AgentsConstants.SessionStreamType), ct);

    /// <summary>
    /// Closes a turn with what it cost in time, what the provider reported it used, and the ledger entry
    /// the money went to. The usage numbers come from the same <see cref="AgentTurnResult"/> the booking
    /// was made from — one writer, one read — and <c>bookingId</c> is what lets a reader reconcile them
    /// against the ledger instead of trusting a second copy.
    /// </summary>
    private ValueTask<long> EmitTurnCompletedAsync(
        string streamId,
        string runId,
        int step,
        ResourceEnvelope<AgentProperties> agent,
        AgentTurnResult turn,
        Guid bookingId,
        CancellationToken ct) =>
        events.AppendAsync(new EventAppend(
            streamId,
            runId,
            AgentEventTypes.TurnCompleted,
            $"turn {step} {turn.StopReason}",
            new JsonObject
            {
                ["turn"] = step,
                ["runId"] = runId,
                ["stopReason"] = turn.StopReason.ToString(),
                ["model"] = turn.ModelId ?? agent.Properties.ModelId,
                ["provider"] = turn.ProviderId,
                ["effort"] = agent.Properties.Effort,
                ["llmMs"] = turn.LlmMs,
                ["usage"] = new JsonObject
                {
                    ["input"] = turn.Usage.InputTokens,
                    ["output"] = turn.Usage.OutputTokens,
                    ["cacheRead"] = turn.Usage.CacheReadInputTokens,
                    ["cacheWrite"] = turn.Usage.CacheWriteInputTokens,
                },
                ["bookingId"] = bookingId == Guid.Empty ? null : bookingId.ToString("N"),
                ["booked"] = bookingId != Guid.Empty,
            },
            RenderLane.Log,
            agent.Name,
            null,
            AgentsConstants.SessionStreamType), ct);
}
