using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// The model history is built **only** from the session log. Compaction appends a
/// <c>agents.session.compacted</c> event and records a surface op; the original events are never
/// mutated, so a replay always reconstructs the same transcript.
/// </summary>
public sealed class SessionLog(IEventLog events)
{
    /// <summary>Appends one transcript message under a session stream.</summary>
    public async ValueTask<long> AppendMessageAsync(string streamId, string? subStreamId, InputMessage message, CancellationToken ct) =>
        await events.AppendAsync(new EventAppend(
            streamId,
            subStreamId,
            AgentSessionEvents.Message,
            message.Role,
            new JsonObject
            {
                ["role"] = message.Role,
                ["blocks"] = new JsonArray(message.Content
                    .Select(b => System.Text.Json.JsonSerializer.SerializeToNode(b, typeof(ContentBlock), JsonWire.Options))
                    .ToArray()),
            },
            RenderLane.Output | RenderLane.Log,
            subStreamId,
            null,
            AgentsConstants.StreamType), ct);

    /// <summary>Appends a compaction marker and returns its sequence.</summary>
    public async ValueTask<long> CompactAsync(
        string streamId,
        string? subStreamId,
        long fromSeq,
        long toSeq,
        string summary,
        CancellationToken ct) =>
        await events.AppendAsync(new EventAppend(
            streamId,
            subStreamId,
            AgentSessionEvents.Compacted,
            "compacted",
            new JsonObject
            {
                ["fromSeq"] = fromSeq,
                ["toSeq"] = toSeq,
                ["summary"] = summary,
            },
            RenderLane.Log,
            subStreamId,
            null,
            AgentsConstants.StreamType), ct);

    /// <summary>
    /// Rebuilds the model history from the log up to <paramref name="upToSeq"/> (inclusive).
    /// A bounded read stops at the requested sequence — no cancellation timeout is involved.
    /// </summary>
    public async ValueTask<IReadOnlyList<InputMessage>> DeriveAsync(
        string streamId,
        long upToSeq,
        CancellationToken ct)
    {
        var messages = new List<InputMessage>();
        var compactions = new List<(long From, long To, string Summary)>();
        var buffer = new Dictionary<long, InputMessage>();

        await foreach (var evt in events.TailAsync(streamId, 0, ct))
        {
            if (evt.Seq > upToSeq)
                break;

            switch (evt.Type)
            {
                case AgentSessionEvents.Message:
                    if (MapMessage(evt.Payload) is { } message)
                        buffer[evt.Seq] = message;
                    break;
                case AgentSessionEvents.Compacted:
                    compactions.Add((
                        evt.Payload?["fromSeq"]?.GetValue<long>() ?? 0,
                        evt.Payload?["toSeq"]?.GetValue<long>() ?? 0,
                        evt.Payload?["summary"]?.GetValue<string>() ?? ""));
                    break;
            }

            // The bound is an existing sequence (the caller persists LastSeq after every append),
            // so the read terminates without a cancellation timeout.
            if (evt.Seq == upToSeq)
                break;
        }

        // Drop every message inside a compacted range and replace it with one summary card.
        var dropped = new HashSet<long>();
        foreach (var (from, to, _) in compactions)
        {
            foreach (var seq in buffer.Keys.Where(s => s >= from && s <= to))
                dropped.Add(seq);
        }

        var cards = compactions
            .OrderBy(c => c.From)
            .Select(c => new InputMessage("user", [new TextBlock(CompactCard(c.Summary))]));

        messages.AddRange(cards);
        foreach (var seq in buffer.Keys.Where(s => !dropped.Contains(s)).OrderBy(s => s))
            messages.Add(buffer[seq]);

        TranscriptInvariants.Validate(messages);
        TranscriptInvariants.ValidateThinking(messages);
        return messages;
    }

    private static string CompactCard(string summary) =>
        "[session-compacted] earlier turns replaced by a summary:\n" + summary;

    internal static InputMessage? MapMessage(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
            return null;
        var role = obj["role"]?.GetValue<string>() ?? "user";
        var content = new List<ContentBlock>();
        if (obj["blocks"] is JsonArray blocks)
        {
            foreach (var item in blocks)
            {
                if (item is null)
                    continue;
                var block = item.Deserialize<ContentBlock>(JsonWire.Options);
                if (block is not null)
                    content.Add(block);
            }
        }

        return new InputMessage(role, content);
    }
}
