using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Nodes;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Registry;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Agents.Abstractions.Workspace;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Extensions.Configuration;

namespace Burbuja.Agents.Runtime;

public sealed class AgentRunner(
    JobStateStore states,
    IResourceStore store,
    ILeaseStore leases,
    IEventLog events,
    LlmProviderRouter llmRouter,
    ICostCalculator costs,
    IBudgetLedger ledger,
    ICache cache,
    IEnumerable<IAgentTool> tools,
    ToolBatchExecutor batch,
    SteeringInbox steering,
    CancellationRegistry cancel,
    IWorkspaceProvider workspaces,
    IRepoRegistry? repos,
    IArtifactStore artifacts,
    IRedactor redactor,
    IOperationTracker ops,
    INodeDirectory nodes,
    TimeProvider time,
    IConfiguration configuration,
    Burbuja.Core.Abstractions.Approvals.IApprovalService approvals)
{
    public async Task RunAsync(string jobKey, Guid runId, AgentRunRole role, string? briefOverride, Guid? parentRunId, Guid? groupId, CancellationToken ct, bool ownsTerminal = true)
    {
        using var runScope = JobExecutionScope.Run(runId, ownsTerminal);
        var job = await states.GetAsync(jobKey, ct);
        if (job is null)
            return;
        if (JobExecution.IsTerminal(job.Properties.Status) && job.Properties.Status != JobExecutionStatus.AwaitingApproval)
        {
            await PersistRunAsync(job, runId, role, parentRunId, groupId, job.Properties.WorkerNodeId ?? "", ct);
            return;
        }

        var nodeId = configuration["Kernel:NodeId"];
        if (string.IsNullOrWhiteSpace(nodeId))
            nodeId = Environment.GetEnvironmentVariable("HOSTNAME") is { Length: > 0 } hostname ? hostname : Environment.MachineName;
        var leaseTtl = TimeSpan.TryParse(configuration["Runtime:LeaseTtl"], out var lt) ? lt : TimeSpan.FromSeconds(45);
        var leaseName = AgentsConstants.LeaseId(jobKey + "/" + runId.ToString("N"));
        var lease = await leases.AcquireAsync(leaseName, nodeId, leaseTtl, ct);
        if (lease is null)
            return;

        var streamId = AgentsConstants.StreamId(job.Id);
        var scenario = job.Tags.TryGetValue(AgentsConstants.ScenarioTag, out var tag) ? tag : "";
        AgentAmbient.Value = new AgentAmbient.State(
            jobKey,
            job.Id,
            runId.ToString("N"),
            scenario,
            role,
            briefOverride ?? job.Properties.Spec.Brief,
            job.Properties.Spec.Language,
            job.Tags);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel.TokenFor(jobKey));
            job = await states.MutateAsync(jobKey, p =>
            {
                if (JobExecution.IsTerminal(p.Status))
                    throw new OperationCanceledException("A terminal job generation cannot be reopened.");
                p.Status = JobExecutionStatus.Running;
                p.RunAttempts++;
                p.WorkerNodeId = nodeId;
                p.StartedAt ??= time.GetUtcNow();
                p.ActiveRunId = runId;
                p.GroupId = groupId ?? p.GroupId;
                p.State ??= new AgentRunState(1, 0, 0, 0, 0, false, null, null, lease.FencingToken, [], null, null, null, null);
                var keepApproval = p.State.PendingBatch is { Calls.Count: > 0 };
                p.State = p.State with
                {
                    LeaseToken = lease.FencingToken,
                    AwaitingApprovalId = keepApproval ? p.State.AwaitingApprovalId : null,
                };
                if (!keepApproval)
                    p.PendingApprovalId = null;
            }, ct, ProvisioningState.Updating);

            await PersistRunAsync(job, runId, role, parentRunId, groupId, nodeId, ct);
            await events.AppendAsync(new EventAppend(streamId, runId.ToString("N"), AgentEventTypes.Progress, "running", null, RenderLane.Log, job.Name, null, AgentsConstants.StreamType), ct);

            var handle = await OpenWorkspaceAsync(job, ct);
            var maxTurns = job.Properties.Spec.MaxTurns ?? job.Properties.Budgets?.MaxTurns ?? ReadInt(configuration, "Runtime:MaxTurns", 80);
            var maxUsd = job.Properties.Spec.MaxBudgetUsd ?? job.Properties.Budgets?.MaxUsd ?? ReadDecimal(configuration, "Budget:DefaultJobUsd", 5m);

            // M10 H3: the DAY's stop, read before a single turn is invoked. Here rather than inside the
            // per-turn loop below because a job that has already run its first turn has already spent, and
            // `Budget:DailyHardStopUsd` is read from configuration at THIS instant — never from the `Budgets`
            // snapshot — so an operator who raises the stop while a job is parked is answered by the next
            // run rather than by a re-PUT. The park is `Blocked`: not `awaitingApproval`, because nobody can
            // approve money.
            if (await DailyHardStop.ReadAsync(configuration, ledger, time, ct).ConfigureAwait(false) is { } hardStop)
            {
                await events.AppendAsync(new EventAppend(
                    streamId, runId.ToString("N"), HardStopReading.EventType, "hardstop", hardStop.ToJson(),
                    RenderLane.Log | RenderLane.Output, job.Name, null, AgentsConstants.StreamType), ct);
                await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Blocked, hardStop.Sentence, ct, ownsTerminal);
                return;
            }

            var checkpoint = Checkpoint(job.Properties, runId);
            var isolated = !string.IsNullOrEmpty(briefOverride) && checkpoint is null;
            if (checkpoint?.ModelInFlight == true)
            {
                job = await states.MutateAsync(jobKey, p =>
                {
                    var run = Checkpoint(p, runId)!;
                    run.ModelInFlight = false;
                    run.InterruptedModelCalls++;
                    p.CostIsPartial = true;
                    p.Notes.Add("A model request was interrupted before its response was recorded; its usage is unknown.");
                }, ct);
                if (Checkpoint(job.Properties, runId)!.InterruptedModelCalls > 1)
                {
                    await FinishAsync(jobKey, runId, streamId, null, JobExecutionStatus.Blocked,
                        "InterruptedModelCall: recovery retry limit reached; cost is partial and review is required.", ct, ownsTerminal);
                    return;
                }
            }

            // Fan-out workers share one job envelope but own an isolated transcript, so a
            // PendingBatch written by one worker must never be executed by another run.
            if (!isolated && job.Properties.State?.PendingBatch is { Calls.Count: > 0 } pending)
            {
                if (await ExecutePendingAsync(jobKey, job, streamId, runId, handle, pending, parentRunId, linked.Token))
                    return;
                job = await states.GetAsync(jobKey, ct) ?? job;
            }

            checkpoint = Checkpoint(job.Properties, runId);
            if (checkpoint?.LastStopReason == LlmStopReason.EndTurn)
            {
                var previousState = job.Properties.State ?? EmptyState();
                if (handle is not null && !previousState.FinishNudged &&
                    !previousState.Transcript.Any(message => message.Content.OfType<ToolResultBlock>().Any()))
                    job = await states.MutateAsync(jobKey, p =>
                    {
                        var transcript = p.State!.Transcript.ToList();
                        transcript.Add(new InputMessage("user", [new TextBlock("Please finish and commit.")]));
                        p.State = p.State with { FinishNudged = true, Transcript = transcript };
                        Checkpoint(p, runId)!.LastStopReason = null;
                    }, ct);
                else
                {
                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Succeeded, null, ct, ownsTerminal);
                    return;
                }
            }

            var isolatedTranscript = isolated
                ? new List<InputMessage> { new("user", [new TextBlock(briefOverride!)]) }
                : new List<InputMessage>();
            var isolatedTurn = 0;

            while (!linked.IsCancellationRequested)
            {
                if (cancel.IsCanceled(jobKey))
                {
                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Cancelled, null, ct, ownsTerminal);
                    return;
                }

                    if (!await leases.RenewAsync(leaseName, nodeId, lease.FencingToken, leaseTtl, ct))
                    return;

                var steered = steering.Drain(jobKey);
                foreach (var text in steered)
                {
                    await events.AppendAsync(new EventAppend(streamId, runId.ToString("N"), AgentEventTypes.SteeringReceived, text, new JsonObject { ["text"] = text }, RenderLane.Log, job.Name, null, AgentsConstants.StreamType), ct);
                    job = await states.MutateAsync(jobKey, p =>
                    {
                        var transcript = p.State?.Transcript.ToList() ?? [];
                        transcript.Add(new InputMessage("user", [new TextBlock(text)]));
                        p.State = (p.State ?? EmptyState()) with { Transcript = transcript };
                    }, ct);
                }

                job = await states.GetAsync(jobKey, ct) ?? job;
                var state = job.Properties.State ?? EmptyState();
                var turn = isolated ? isolatedTurn : state.Turn;
                if (turn >= maxTurns)
                {
                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.TurnsExceeded, null, ct, ownsTerminal);
                    return;
                }

                if (state.ConsecutiveFailures >= 12)
                {
                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Failed, "breaker", ct, ownsTerminal);
                    return;
                }

                var spend = await ledger.GetJobSpendAsync(ParseJobId(job.Id), ct);
                if (spend >= maxUsd)
                {
                    if (state.BudgetExtensions < 6)
                    {
                        await events.AppendAsync(new EventAppend(streamId, null, AgentEventTypes.BudgetExceeded, "budget", null, RenderLane.Log, job.Name, spend, AgentsConstants.StreamType), ct);
                        maxUsd += 20m;
                        job = await states.MutateAsync(jobKey, p =>
                        {
                            p.State = (p.State ?? EmptyState()) with { BudgetExtensions = (p.State?.BudgetExtensions ?? 0) + 1 };
                            if (p.Budgets is { } b)
                                p.Budgets = b with { MaxUsd = b.MaxUsd + 20m };
                            else
                                p.Budgets = new JobBudgets(maxUsd, maxTurns, ReadDecimal(configuration, "Budget:DailySoftCapUsd", 0m), ReadDecimal(configuration, "Budget:DailyHardStopUsd", 0m));
                        }, ct);
                        await events.AppendAsync(new EventAppend(streamId, null, AgentEventTypes.BudgetExtended, "extended", null, RenderLane.Log, job.Name, 20m, AgentsConstants.StreamType), ct);
                    }
                    else
                    {
                        await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.BudgetExceeded, "budget", ct, ownsTerminal);
                        return;
                    }
                }

                var transcript = isolated ? isolatedTranscript.ToList() : state.Transcript.ToList();
                transcript = TranscriptCompactor.Compact(transcript, out var compacted).ToList();
                if (compacted)
                {
                    var card = StateCardBuilder.Build(transcript, briefOverride ?? job.Properties.Spec.Brief, turn);
                    transcript.Insert(0, new InputMessage("user", [new TextBlock(card)]));
                    await events.AppendAsync(new EventAppend(streamId, null, AgentEventTypes.Compact, "compact", null, RenderLane.Log, job.Name, null, AgentsConstants.StreamType), ct);
                }

                if (transcript.Count == 0)
                    transcript.Add(new InputMessage("user", [new TextBlock(briefOverride ?? job.Properties.Spec.Brief)]));

                var toolSpecs = tools.Select(t => new ToolSpec(t.Descriptor.Name, t.Descriptor.Description, t.Descriptor.InputSchema)).ToArray();
                // The job's own model, when it named one: the request and the quote below must agree
                // on the same resolved id, or a booked number would describe a different model.
                var modelId = string.IsNullOrWhiteSpace(job.Properties.ModelId) ? AgentsConstants.DefaultModelId : job.Properties.ModelId;
                var request = new LlmRequest(
                    modelId,
                    transcript,
                    "You are a coding agent.",
                    toolSpecs,
                    new ToolChoiceAuto(),
                    4096,
                    null,
                    job.Properties.Effort);
                TranscriptInvariants.Validate(request.Messages);

                LlmResponse response;
                // M9 T1: the job path emits the same telemetry pair a stateful agent's session emits, so a
                // trajectory reads the same way whichever lane produced it.
                checkpoint = Checkpoint(job.Properties, runId);
                var invokeStarted = time.GetTimestamp();
                var llmMs = checkpoint?.LlmMs ?? 0;
                if (checkpoint?.PendingResponse is { } recordedResponse)
                    response = recordedResponse;
                else
                {
                    job = await states.MutateAsync(jobKey, p =>
                    {
                        if (Checkpoint(p, runId) is { } run)
                        {
                            run.ModelInFlight = true;
                            run.ModelStartedAt = time.GetUtcNow();
                            run.ResponseBooked = false;
                            run.LastStopReason = null;
                        }
                    }, ct);
                await events.AppendAsync(new EventAppend(
                    streamId, runId.ToString("N"), AgentEventTypes.TurnStarted, $"turn {turn}",
                    new JsonObject { ["turn"] = turn, ["runId"] = runId.ToString("N"), ["model"] = modelId, ["effort"] = job.Properties.Effort },
                    RenderLane.Log, job.Name, null, AgentsConstants.StreamType), ct);
                try
                {
                    response = await InvokeWithRetryAsync(request, nodeId, linked.Token);
                }
                catch (OperationCanceledException) when (cancel.IsCanceled(jobKey))
                {
                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Cancelled, null, CancellationToken.None, ownsTerminal);
                    return;
                }

                    llmMs = (int)time.GetElapsedTime(invokeStarted).TotalMilliseconds;
                    job = await states.MutateAsync(jobKey, p =>
                    {
                        if (Checkpoint(p, runId) is { } run)
                        {
                            run.PendingResponse = response;
                            run.ModelInFlight = false;
                            run.InterruptedModelCalls = 0;
                            run.LlmMs = llmMs;
                        }
                    }, ct);
                }

                var bookKey = $"booked:{job.Id}:{runId:N}:{turn}:worker";
                var bookingId = Checkpoint(job.Properties, runId)?.ResponseBooked == true ? EntityIds.Derive(bookKey) : Guid.Empty;
                checkpoint = Checkpoint(job.Properties, runId);
                if (checkpoint is not null ? !checkpoint.ResponseBooked : await cache.TryAddAsync(bookKey, true, TimeSpan.FromHours(24), ct))
                {
                    var usd = costs.Quote(modelId, response.Usage);
                    bookingId = await ledger.BookAsync(new BudgetBooking(ParseJobId(job.Id), runId, BudgetKind.Worker, usd, response.Usage.InputTokens, response.Usage.OutputTokens)
                    { IdempotencyId = EntityIds.Derive(bookKey), BookedAt = checkpoint?.ModelStartedAt }, ct);
                    job = await states.MutateAsync(jobKey, p =>
                    {
                        var c = p.Cost;
                        p.Cost = c with
                        {
                            WorkerUsd = c.WorkerUsd + usd,
                            InputTokens = c.InputTokens + response.Usage.InputTokens,
                            OutputTokens = c.OutputTokens + response.Usage.OutputTokens,
                        };
                        if (Checkpoint(p, runId) is { } run)
                        {
                            run.ResponseBooked = true;
                            run.CostUsd += usd;
                        }
                    }, ct);
                }

                await events.AppendAsync(new EventAppend(
                    streamId, runId.ToString("N"), AgentEventTypes.TurnCompleted, $"turn {turn} {response.StopReason}",
                    new JsonObject
                    {
                        ["turn"] = turn,
                        ["runId"] = runId.ToString("N"),
                        ["stopReason"] = response.StopReason.ToString(),
                        ["model"] = modelId,
                        ["provider"] = llmRouter.Provider.Id,
                        ["effort"] = job.Properties.Effort,
                        ["llmMs"] = llmMs,
                        ["usage"] = new JsonObject
                        {
                            ["input"] = response.Usage.InputTokens,
                            ["output"] = response.Usage.OutputTokens,
                            ["cacheRead"] = response.Usage.CacheReadInputTokens,
                            ["cacheWrite"] = response.Usage.CacheWriteInputTokens,
                        },
                        ["bookingId"] = bookingId == Guid.Empty ? null : bookingId.ToString("N"),
                        ["booked"] = bookingId != Guid.Empty,
                    },
                    RenderLane.Log, job.Name, null, AgentsConstants.StreamType), ct);

                foreach (var block in response.Blocks)
                {
                    if (block is TextBlock text)
                        await events.AppendAsync(new EventAppend(streamId, runId.ToString("N"), AgentEventTypes.AssistantText, redactor.Redact(text.Text), null, RenderLane.Output, job.Name, null, AgentsConstants.StreamType), ct);
                    if (block is ToolUseBlock use)
                        await events.AppendAsync(new EventAppend(streamId, use.Id, AgentEventTypes.ToolUse, use.Name, use.Input.DeepClone(), RenderLane.Log, use.Name, null, AgentsConstants.StreamType), ct);
                }

                transcript.Add(new InputMessage("assistant", response.Blocks));
                if (isolated)
                {
                    isolatedTurn = turn + 1;
                    isolatedTranscript = transcript;
                }
                else
                {
                    job = await states.MutateAsync(jobKey, p =>
                    {
                        p.State = (p.State ?? EmptyState()) with
                        {
                            Turn = turn + 1, Transcript = transcript, ConsecutiveFailures = 0,
                            PendingBatch = response.StopReason == LlmStopReason.ToolUse
                                ? new PendingToolBatch(Guid.NewGuid().ToString("N"), response.Blocks.OfType<ToolUseBlock>().ToList(), null)
                                : null,
                        };
                        if (Checkpoint(p, runId) is { } run)
                        {
                            run.PendingResponse = null;
                            run.LastStopReason = response.StopReason;
                        }
                    }, ct);
                    state = job.Properties.State ?? EmptyState();
                }

                if (response.StopReason == LlmStopReason.EndTurn)
                {
                    var didWork = transcript.Any(m => m.Content.OfType<ToolResultBlock>().Any());
                    if (handle is not null && !state.FinishNudged && !didWork)
                    {
                        job = await states.MutateAsync(jobKey, p =>
                        {
                            var t = p.State?.Transcript.ToList() ?? [];
                            t.Add(new InputMessage("user", [new TextBlock("Please finish and commit.")]));
                            p.State = (p.State ?? EmptyState()) with { FinishNudged = true, Transcript = t };
                            if (Checkpoint(p, runId) is { } run)
                                run.LastStopReason = null;
                        }, ct);
                        continue;
                    }

                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Succeeded, null, ct, ownsTerminal);
                    return;
                }

                if (response.StopReason == LlmStopReason.MaxTokens)
                {
                    if (state.MaxTokenContinues < 3)
                    {
                        await states.MutateAsync(jobKey, p =>
                        {
                            p.State = (p.State ?? EmptyState()) with { MaxTokenContinues = (p.State?.MaxTokenContinues ?? 0) + 1 };
                        }, ct);
                        continue;
                    }

                    await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Failed, "max_tokens", ct, ownsTerminal);
                    return;
                }

                if (response.StopReason == LlmStopReason.ToolUse)
                {
                    var calls = response.Blocks.OfType<ToolUseBlock>().ToList();
                    if (!isolated)
                    {
                        await states.MutateAsync(jobKey, p =>
                        {
                            p.State = (p.State ?? EmptyState()) with { PendingBatch = new PendingToolBatch(Guid.NewGuid().ToString("N"), calls, null) };
                        }, ct);
                    }

                    var (execCtx, subject) = await BuildBatchContextAsync(job, streamId, runId, handle, parentRunId, linked.Token);
                    var executed = await batch.ExecuteAsync(subject, streamId, runId.ToString("N"), calls, execCtx, linked.Token);
                    if (executed.Park is not null)
                    {
                        await states.MutateAsync(jobKey, p =>
                        {
                            p.Status = JobExecutionStatus.AwaitingApproval;
                            p.PendingApprovalId = executed.Park.ApprovalId;
                            p.State = (p.State ?? EmptyState()) with { AwaitingApprovalId = executed.Park.ApprovalId, Transcript = isolated ? p.State?.Transcript ?? [] : transcript };
                        }, ct, ProvisioningState.Updating);
                        return;
                    }

                    var redactedResults = executed.Results.Select(r =>
                        r with { Content = JsonNode.Parse(redactor.Redact(r.Content.ToJsonString())) ?? r.Content }).ToList();
                    transcript.Add(new InputMessage("user", redactedResults));
                    if (isolated)
                        isolatedTranscript = transcript;
                    else
                    {
                        job = await states.MutateAsync(jobKey, p =>
                        {
                            p.State = (p.State ?? EmptyState()) with { PendingBatch = null, Transcript = transcript };
                            p.Status = JobExecutionStatus.Running;
                        }, ct);
                    }

                    if (turn > 0 && turn % 5 == 0 && handle is not null)
                        await workspaces.CaptureDiffAsync(handle, ct);
                }
            }

            if (cancel.IsCanceled(jobKey))
                await FinishAsync(jobKey, runId, streamId, handle, JobExecutionStatus.Cancelled, null, CancellationToken.None, ownsTerminal);
        }
        catch (InterruptedToolExecutionException ex)
        {
            await FinishAsync(jobKey, runId, streamId, null, JobExecutionStatus.Blocked, ex.Message,
                CancellationToken.None, ownsTerminal);
        }
        finally
        {
            await leases.ReleaseAsync(leaseName, nodeId, lease.FencingToken, CancellationToken.None);
            AgentAmbient.Value = null;
        }
    }

    private async Task<LlmResponse> InvokeWithRetryAsync(LlmRequest request, string nodeId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                var response = await llmRouter.Provider.InvokeAsync(request, ct);
                nodes.ObserveSuccess(nodeId);
                return response;
            }
            catch (LlmThrottledException)
            {
                nodes.ObserveThrottle(nodeId);
                await Task.Delay(TimeSpan.FromMilliseconds(15), time, ct);
            }
        }

        throw new LlmThrottledException();
    }

    public async ValueTask SettleAsync(string jobKey, JobExecutionStatus status, string? error, CancellationToken ct)
    {
        var job = await states.GetAsync(jobKey, ct);
        if (job is null)
            return;
        var runId = job.Properties.ActiveRunId ?? job.Properties.Recovery?.Runs.FirstOrDefault()?.RunId ?? Guid.NewGuid();
        using var scope = JobExecutionScope.Run(runId, ownsTerminal: true);
        await FinishAsync(jobKey, runId, AgentsConstants.StreamId(job.Id), null, status, error, ct);
    }

    private async Task FinishAsync(
        string jobKey,
        Guid runId,
        string streamId,
        WorkspaceHandle? handle,
        JobExecutionStatus status,
        string? error,
        CancellationToken ct,
        bool ownsTerminal = true)
    {
        if (!ownsTerminal)
        {
            var worker = await states.MutateAsync(jobKey, p =>
            {
                if (p.Status == JobExecutionStatus.Cancelled && status != JobExecutionStatus.Cancelled)
                    throw new OperationCanceledException("Accepted cancellation is final.");
                p.Status = status;
                p.Error = error;
                p.PendingApprovalId = null;
            }, ct);
            await PersistRunAsync(worker, runId, AgentAmbient.Value?.Role ?? AgentRunRole.Worker,
                null, worker.Properties.GroupId, worker.Properties.WorkerNodeId ?? "", ct);
            return;
        }
        if (handle is not null && status == JobExecutionStatus.Succeeded)
        {
            try
            {
                await workspaces.CommitAsync(handle, "agent work", ct);
                var patch = await workspaces.CaptureDiffAsync(handle, ct);
                await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(patch.Diff));
                await artifacts.PutAsync(ArtifactKey(jobKey, "patch.diff"), ms, "text/plain", null, ct);
            }
            catch
            {
                // commit is best-effort when the worktree has nothing to commit
            }
        }

        var env = await states.MutateAsync(jobKey, p =>
        {
            if (p.Status == JobExecutionStatus.Cancelled && status != JobExecutionStatus.Cancelled)
                throw new OperationCanceledException("Accepted cancellation is final.");
            p.Status = status;
            p.Error = error;
            p.EndedAt ??= time.GetUtcNow();
            p.PendingApprovalId = null;
        }, ct, ProvisioningState.Updating);

        if (status is JobExecutionStatus.Cancelled)
            await ops.CancelAsync(env.Properties.OperationId ?? "", "cancelled", ct);
        else if (status is JobExecutionStatus.Succeeded)
            await ops.SucceedAsync(env.Properties.OperationId ?? "", null, ct);
        else
            await ops.FailAsync(env.Properties.OperationId ?? "", ArmError.Conflict(status.ToString(), error ?? status.ToString()), ct);

        var transcript = env.Properties.State?.Transcript ?? [];
        await using var ts = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(redactor.Redact(System.Text.Json.JsonSerializer.Serialize(transcript, JsonWire.Options))));
        await artifacts.PutAsync(ArtifactKey(jobKey, "transcript.json"), ts, "application/json", null, ct);

        var recorded = false;
        long cursor = 0;
        while (true)
        {
            var count = 0;
            await foreach (var evt in events.ReadRangeAsync(streamId, cursor, long.MaxValue, 512, null, ct))
            {
                count++;
                cursor = evt.Seq + 1;
                if (evt.Type == AgentEventTypes.Result && evt.SubStreamId == runId.ToString("N"))
                    recorded = true;
            }
            if (recorded || count < 512)
                break;
        }
        if (!recorded)
            await events.AppendAsync(new EventAppend(
            streamId,
            runId.ToString("N"),
            AgentEventTypes.Result,
            status.ToString(),
            new JsonObject { ["status"] = status.ToString() },
            RenderLane.Output | RenderLane.Log,
            env.Name,
            env.Properties.Cost.TotalUsd,
            AgentsConstants.StreamType), ct);
        env = await states.MutateAsync(jobKey, _ => { }, ct, status is JobExecutionStatus.Succeeded or JobExecutionStatus.Cancelled
            ? ProvisioningState.Succeeded : ProvisioningState.Failed);
        await PersistRunAsync(env, runId, AgentAmbient.Value?.Role ?? Checkpoint(env.Properties, runId)?.Role ?? AgentRunRole.Worker,
            null, env.Properties.GroupId, env.Properties.WorkerNodeId ?? "", ct);
    }

    private async Task<WorkspaceHandle?> OpenWorkspaceAsync(ResourceEnvelope<JobProperties> job, CancellationToken ct)
    {
        if (job.Properties.State is { WorkspacePath: { Length: > 0 } path } existing)
            return new WorkspaceHandle(job.Id, path, existing.VolumeId ?? "", existing.WorkspaceBranch ?? "", existing.WorkspaceBaseSha ?? "");

        var repoId = job.Properties.Spec.RepoId ?? "demo";
        var facts = repos?.Get(repoId);
        try
        {
            var handle = await workspaces.OpenAsync(job.Id, repoId, facts?.BaseBranch ?? "main", ct);
            await states.MutateAsync(job.Id.ToLowerInvariant(), p =>
            {
                p.State = (p.State ?? EmptyState()) with
                {
                    WorkspacePath = handle.Path,
                    WorkspaceBranch = handle.Branch,
                    WorkspaceBaseSha = handle.BaseSha,
                    VolumeId = handle.VolumeId,
                };
            }, ct);
            return handle;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> ExecutePendingAsync(
        string jobKey,
        ResourceEnvelope<JobProperties> job,
        string streamId,
        Guid runId,
        WorkspaceHandle? handle,
        PendingToolBatch pending,
        Guid? parentRunId,
        CancellationToken ct)
    {
        var (execCtx, subject) = await BuildBatchContextAsync(job, streamId, runId, handle, parentRunId, ct);
        var resumeApproval = job.Properties.PendingApprovalId ?? job.Properties.State?.AwaitingApprovalId;
        var executed = await batch.ExecuteAsync(subject, streamId, runId.ToString("N"), pending.Calls, execCtx, ct, resumeApproval);
        if (executed.Park is not null)
        {
            await states.MutateAsync(jobKey, p =>
            {
                p.Status = JobExecutionStatus.AwaitingApproval;
                p.PendingApprovalId = executed.Park.ApprovalId;
                p.State = (p.State ?? EmptyState()) with { AwaitingApprovalId = executed.Park.ApprovalId };
            }, ct, ProvisioningState.Updating);
            return true;
        }

        var redactedResults = executed.Results.Select(r =>
            r with { Content = JsonNode.Parse(redactor.Redact(r.Content.ToJsonString())) ?? r.Content }).ToList();
        await states.MutateAsync(jobKey, p =>
        {
            var transcript = p.State?.Transcript.ToList() ?? [];
            transcript.Add(new InputMessage("user", redactedResults));
            p.State = (p.State ?? EmptyState()) with { PendingBatch = null, Transcript = transcript };
            p.Status = JobExecutionStatus.Running;
            p.PendingApprovalId = null;
        }, ct);
        return false;
    }

    private async Task<(ToolExecutionContext Ctx, JobGateSubject Subject)> BuildBatchContextAsync(
        ResourceEnvelope<JobProperties> job,
        string streamId,
        Guid runId,
        WorkspaceHandle? handle,
        Guid? parentRunId,
        CancellationToken ct)
    {
        var wsRoot = handle?.Path ?? Path.GetTempPath();
        var toolTimeout = TimeSpan.TryParse(configuration["Runtime:ToolTimeout"], out var tt) ? tt : TimeSpan.FromMinutes(10);
        var execCtx = new ToolExecutionContext(wsRoot, streamId, runId.ToString("N"), job.Id, toolTimeout, new Dictionary<string, string>());
        var parent = job.Id.Contains("/jobs/", StringComparison.OrdinalIgnoreCase)
            ? job.Id[..job.Id.LastIndexOf("/jobs/", StringComparison.OrdinalIgnoreCase)]
            : job.Id;
        var ws = await store.GetAsync(parent.ToLowerInvariant(), ct);
        var profile = ws?.Properties["profile"]?.GetValue<string>() ?? "DevFullAuto";
        var verify = ws?.Properties["verify"] is JsonArray arr
            ? arr.Select(n => n?.GetValue<string>() ?? "").ToArray()
            : [];
        // Adjacency facts for the agent this job runs as. A job with no agent has none, so every
        // agent-to-agent tool call escalates rather than being silently allowed.
        var peers = await AgentAdjacency.PeersAsync(store, job.Properties.AgentId, job.Properties.TeamId, ct);
        var subject = new JobGateSubject(
            Key: job.Id.ToLowerInvariant(),
            ParseJobId(job.Id),
            job.Id,
            job.Properties.Spec,
            profile,
            verify,
            parentRunId is not null,
            job.Properties.State?.Turn ?? 0,
            job.Properties.Cost.TotalUsd,
            peers,
            states,
            approvals);
        return (execCtx, subject);
    }

    private static string ArtifactKey(string jobKey, string name)
    {
        var prefix = jobKey.Trim().TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = "job";
        return prefix + "/" + name;
    }

    private async Task PersistRunAsync(
        ResourceEnvelope<JobProperties> job,
        Guid runId,
        AgentRunRole role,
        Guid? parentRunId,
        Guid? groupId,
        string nodeId,
        CancellationToken ct)
    {
        var id = job.Id + "/runs/" + runId.ToString("N")[..8];
        var envelope = new ResourceEnvelope<JsonObject>(
            id,
            runId.ToString("N")[..8],
            $"{AgentsConstants.Namespace}/workspaces/jobs/runs",
            "local",
            new Dictionary<string, string>(),
            System.Text.Json.JsonSerializer.SerializeToNode(new RunProperties
            {
                RunId = runId,
                JobId = ParseJobId(job.Id),
                ParentRunId = parentRunId,
                GroupId = groupId,
                Role = role,
                Attempt = job.Properties.RunAttempts,
                WorkerNodeId = nodeId,
                Status = Checkpoint(job.Properties, runId)?.Status ?? job.Properties.Status,
                Transcript = job.Properties.State?.Transcript.ToList() ?? [],
                CostUsd = Checkpoint(job.Properties, runId)?.CostUsd ?? 0,
            }, JsonWire.Options)!.AsObject(),
            ProvisioningState.Succeeded,
            job.SystemData,
            "");
        try
        {
            await store.UpsertAsync(envelope, null, ct);
        }
        catch
        {
            // run projection is informational
        }
    }

    private static AgentRunState EmptyState() =>
        new(1, 0, 0, 0, 0, false, null, null, null, [], null, null, null, null);

    private static JobRunCheckpoint? Checkpoint(JobProperties job, Guid runId) =>
        job.Recovery?.Runs.FirstOrDefault(run => run.RunId == runId);

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var n) && n > 0 ? n : fallback;

    private static decimal ReadDecimal(IConfiguration configuration, string key, decimal fallback) =>
        decimal.TryParse(configuration[key], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : fallback;

    private static Guid ParseJobId(string canonical) => EntityIds.Derive(canonical);
}
