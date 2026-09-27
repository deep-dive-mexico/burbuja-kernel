using Burbuja.Agents.Abstractions;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Agents.Model;

namespace Burbuja.Agents.Runtime;

public sealed record AgentTurnResult(
    long Seq,
    LlmStopReason StopReason,
    int OutputTokens,
    IReadOnlyList<ContentBlock> Blocks,
    Usage Usage)
{
    public static readonly AgentTurnResult Empty = new(0, LlmStopReason.EndTurn, 0, [], new Usage(0, 0, 0, 0));

    /// <summary>
    /// M9 T1: how long the provider call itself took, in milliseconds. Measured around the ONE await
    /// that is the model call — not derived from the difference between two event timestamps, which is a
    /// property of when the rows were written.
    /// </summary>
    public int LlmMs { get; init; }

    /// <summary>The model and provider this turn was actually sent to, resolved at the call site.</summary>
    public string? ModelId { get; init; }

    public string? ProviderId { get; init; }
}

/// <summary>
/// One model turn for a stateful agent: the history comes from the session log, never from a
/// process-local transcript, so a cold resume reconstructs the same request. M5.1: the turn is
/// handed the agent's tool catalogue, so the caller can loop on <see cref="LlmStopReason.ToolUse"/>
/// — the tools themselves are still executed by <see cref="ToolBatchExecutor"/>, never here.
/// </summary>
public sealed class AgentTurnRunner(SessionLog log, LlmProviderRouter router, TimeProvider clock, IArtifactStore? artifacts = null)
{
    /// <summary>The image source kind the session log keeps for an image held in the artifact store (key in
    /// <c>url</c>). Never sent to a provider: <see cref="ResolveImagesAsync"/> replaces it with base64 first.</summary>
    public const string ArtifactImageKind = "artifact";

    /// <summary>
    /// 2026-09-26 (live review) — artifact-referenced images become base64 blocks at request time, read fresh from
    /// the store. A missing artifact becomes the model's own sentence, as the worker already does at delivery.
    /// </summary>
    internal async ValueTask<IReadOnlyList<InputMessage>> ResolveImagesAsync(IReadOnlyList<InputMessage> history, CancellationToken ct)
    {
        if (!history.Any(m => m.Content.Any(b => b is ImageBlock { Source.Kind: ArtifactImageKind })))
            return history;

        var resolved = new List<InputMessage>(history.Count);
        foreach (var message in history)
        {
            if (!message.Content.Any(b => b is ImageBlock { Source.Kind: ArtifactImageKind }))
            {
                resolved.Add(message);
                continue;
            }

            var blocks = new List<ContentBlock>(message.Content.Count);
            foreach (var block in message.Content)
            {
                if (block is not ImageBlock { Source: { Kind: ArtifactImageKind } source })
                {
                    blocks.Add(block);
                    continue;
                }

                var blob = artifacts is null || source.Url is null ? null : await artifacts.GetAsync(source.Url, ct);
                if (blob is null)
                {
                    blocks.Add(new TextBlock($"(an image was attached as artifact '{source.Url}' and could NOT be read now: "
                        + "say so rather than describing an image you did not get.)"));
                    continue;
                }

                await using var content = blob.Content;
                await using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, ct);
                blocks.Add(new ImageBlock(new ImageSource("base64", source.MediaType, Convert.ToBase64String(buffer.ToArray()))));
            }

            resolved.Add(message with { Content = blocks });
        }

        return resolved;
    }

    public async ValueTask<AgentTurnResult> RunTurnAsync(
        string streamId,
        AgentProperties agent,
        long upToSeq,
        string scenario,
        string runId,
        IReadOnlyList<ToolSpec> tools,
        CancellationToken ct)
    {
        var history = upToSeq <= 0
            ? (IReadOnlyList<InputMessage>)[]
            : await log.DeriveAsync(streamId, upToSeq, ct);
        history = await ResolveImagesAsync(history, ct);

        var llm = router.Provider is NullLlmProvider ? null : router.Provider;
        if (llm is null)
            return AgentTurnResult.Empty;

        var previous = AgentAmbient.Value;
        AgentAmbient.Value = new AgentAmbient.State(
            JobKey: streamId,
            JobId: streamId,
            RunId: runId,
            Scenario: scenario,
            Role: Abstractions.Orchestration.AgentRunRole.Worker,
            Brief: "",
            Language: "en",
            Tags: new Dictionary<string, string>());
        try
        {
            // The provider owns the output ceiling for the model it is actually asked to run;
            // 4096 stays the floor when a provider declares nothing (the scripted one does).
            var capabilities = llm.Capabilities(agent.ModelId);
            var maxTokens = capabilities.MaxOutputTokens > 0 ? capabilities.MaxOutputTokens : 4096;
            // M9 T1: the elapsed time of the ONE call that is the model turn. `TimeProvider` rather than
            // Stopwatch so a fact with a fake clock can assert it deterministically.
            var started = clock.GetTimestamp();
            var response = await llm.InvokeAsync(new LlmRequest(
                agent.ModelId,
                history,
                agent.Persona,
                tools.Count == 0 ? null : tools,
                tools.Count == 0 ? null : new ToolChoiceAuto(),
                maxTokens,
                null,
                agent.Effort), ct);
            var llmMs = (int)clock.GetElapsedTime(started).TotalMilliseconds;

            var seq = await log.AppendMessageAsync(streamId, null, new InputMessage("assistant", response.Blocks), ct);
            return new AgentTurnResult(seq, response.StopReason, response.Usage.OutputTokens, response.Blocks, response.Usage)
            {
                LlmMs = llmMs,
                ModelId = agent.ModelId,
                ProviderId = llm.Id,
            };
        }
        finally
        {
            AgentAmbient.Value = previous;
        }
    }
}
