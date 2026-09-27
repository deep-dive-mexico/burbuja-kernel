using Burbuja.Agents.Abstractions.Llm;

namespace Burbuja.Agents.Runtime;

public static class StateCardBuilder
{
    public const int MaxChars = 4200;

    public static string Build(IReadOnlyList<InputMessage> transcript, string brief, int turn)
    {
        var last = transcript.Count == 0 ? "" : Summarize(transcript[^1]);
        var card = $"turn={turn}\nbrief={brief}\nlast={last}";
        return card.Length <= MaxChars ? card : card[..MaxChars];
    }

    private static string Summarize(InputMessage message)
    {
        var parts = message.Content.Select(b => b switch
        {
            TextBlock t => t.Text,
            ToolUseBlock u => $"tool_use:{u.Name}",
            ToolResultBlock r => $"tool_result:{r.ToolUseId}:{(r.IsError ? "err" : "ok")}",
            _ => b.GetType().Name,
        });
        return string.Join(' ', parts);
    }
}

public static class TranscriptCompactor
{
    public static IReadOnlyList<InputMessage> Compact(IReadOnlyList<InputMessage> transcript, out bool compacted)
    {
        compacted = false;
        if (transcript.Count <= 24)
            return transcript;

        var kept = transcript.ToList();
        while (kept.Count > 16)
        {
            var idx = IndexOfRemovablePair(kept);
            if (idx < 0)
                break;
            if (HasToolUse(kept[idx]) && idx + 1 < kept.Count && HasMatchingResults(kept[idx], kept[idx + 1]))
            {
                kept.RemoveAt(idx + 1);
                kept.RemoveAt(idx);
            }
            else
                kept.RemoveAt(idx);
            compacted = true;
        }

        return kept;
    }

    private static int IndexOfRemovablePair(IReadOnlyList<InputMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (HasToolUse(messages[i]))
            {
                if (i + 1 < messages.Count && HasMatchingResults(messages[i], messages[i + 1]))
                    return i;
                continue;
            }

            if (HasToolResult(messages[i]))
                continue;
            return i;
        }

        return -1;
    }

    private static bool HasToolUse(InputMessage m) => m.Content.OfType<ToolUseBlock>().Any();
    private static bool HasToolResult(InputMessage m) => m.Content.OfType<ToolResultBlock>().Any();

    private static bool HasMatchingResults(InputMessage use, InputMessage result)
    {
        var ids = use.Content.OfType<ToolUseBlock>().Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var results = result.Content.OfType<ToolResultBlock>().ToList();
        return results.Count == ids.Count && results.All(r => ids.Contains(r.ToolUseId));
    }
}

public static class TranscriptInvariants
{
    /// <summary>
    /// A thinking block must stay adjacent to the tool use it produced: it may never be the last
    /// block of a message, or the provider cannot round-trip the signature.
    /// </summary>
    public static void ValidateThinking(IReadOnlyList<InputMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var content = messages[i].Content;
            for (var b = 0; b < content.Count; b++)
            {
                if (content[b] is ThinkingBlock && b == content.Count - 1)
                    throw new InvalidOperationException(
                        $"thinking block at message {i} is the last block; thinking must stay adjacent to its tool use");
            }
        }
    }

    public static void Validate(IReadOnlyList<InputMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var block in message.Content)
            {
                if (block is ToolUseBlock use)
                    pending.Add(use.Id);
                if (block is ToolResultBlock result)
                {
                    if (!pending.Remove(result.ToolUseId))
                        throw new InvalidOperationException($"tool_result without tool_use '{result.ToolUseId}'.");
                }
            }
        }
    }
}
