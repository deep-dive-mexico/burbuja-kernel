using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Channel;

/// <summary>One message a channel put on its surface, as a test lane needs to see it.</summary>
/// <param name="Kind">How it got there: <c>send</c>, <c>edit</c>, <c>dm</c>, <c>rename</c>, <c>thread</c>, <c>channel</c>, <c>delete</c>.</param>
/// <param name="ChannelId">Where it went — a channel id, a DM user id, or a channel being renamed.</param>
/// <param name="Content">The text, or the new name for a rename.</param>
/// <param name="Components">
/// The component custom ids, in order. This is what a fact asserts a click would have been offered:
/// "the card carried <c>pn:apprc:…</c>" is the difference between a card and a paragraph.
/// </param>
/// <param name="MessageId">The message ref the channel kept, so an edit can be told from a send.</param>
public sealed record ChannelOutboxEntry(
    string Kind,
    string ChannelId,
    string Content,
    IReadOnlyList<string> Components,
    string? MessageId = null,
    DateTimeOffset At = default);

/// <summary>
/// P1.6: the seam that makes a chat surface drivable from outside its own process.
///
/// <para><b>Why this lives in Core.</b> The transport belongs to a channel plugin and the ARM resource
/// that exposes it belongs to the resource provider — two assemblies in two plugin load contexts that
/// share nothing but <c>Burbuja.Core.Abstractions</c>. Without a contract here the only ways to drive a
/// button click in a test would be to reference the plugin's assembly (which the resource provider must
/// not) or to leave the click path proven by unit facts alone (which is what the M8 review found: the
/// card was rendered, the ids were right, and nothing proved a click ever reached the kernel).</para>
///
/// <para><b>It is deliberately not a general capability.</b> A channel that answers <c>null</c> here is
/// the production case, and the ARM resource says so instead of offering actions that would fail. Only a
/// transport configured for a test lane (pomnibot's <c>Discord:Transport=fake</c>) implements it — the
/// alternative, a live bot driven by a test, means clicking real buttons in a real guild on every run.</para>
/// </summary>
public interface IChannelOutbox
{
    /// <summary>Everything the channel has put on a surface, oldest first.</summary>
    IReadOnlyList<ChannelOutboxEntry> Sent { get; }

    /// <summary>
    /// Deliver a message as if a human typed it, through the same handler the real gateway calls. The
    /// authorisation decision is the gateway's, not this method's — injecting as a stranger must be
    /// possible, or the "a stranger is dropped" fact cannot be written.
    /// </summary>
    ValueTask InjectAsync(string authorId, string? channelId, string text, bool direct, CancellationToken ct);

    /// <summary>
    /// Deliver a component click as if a human pressed it. <paramref name="modalValues"/> carries the
    /// inputs when the click submits a modal rather than a button.
    /// </summary>
    ValueTask ClickAsync(string customId, string userId, string? channelId, IReadOnlyDictionary<string, string>? modalValues, CancellationToken ct);
}

/// <summary>
/// A channel's outbox plus the two actions, addressed the way an ARM resource addresses them: the JSON
/// shapes the <c>channels/outbox</c> child and the <c>inject</c>/<c>click</c> actions carry.
/// </summary>
public static class ChannelOutboxWire
{
    public static JsonObject ToJson(ChannelOutboxEntry entry) => new()
    {
        ["kind"] = entry.Kind,
        ["channelId"] = entry.ChannelId,
        ["content"] = entry.Content,
        ["components"] = new JsonArray(entry.Components.Select(c => (JsonNode)c!).ToArray()),
        ["messageId"] = entry.MessageId,
        ["at"] = entry.At == default ? null : entry.At.ToString("O"),
    };

    public static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Where(v => v is not null).Select(v => v!.GetValue<string>()).ToList()
            : [];

    public static string? Text(JsonNode? node, string name) =>
        node?[name] is { } value && value.GetValueKind() != System.Text.Json.JsonValueKind.Null
            ? value.GetValue<string>()
            : null;
}
