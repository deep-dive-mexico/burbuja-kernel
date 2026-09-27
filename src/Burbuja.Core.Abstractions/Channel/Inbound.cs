using Burbuja.Core.Abstractions.Events;

namespace Burbuja.Core.Abstractions.Channel;

/// <summary>
/// M14 R11 (request row X8b) — the INBOUND half of a channel, and the seam where a door and the domain meet
/// without either importing the other.
///
/// <para><b>What was missing.</b> <see cref="IChannel"/> is send-only: a channel can be told something, and
/// nothing can tell the kernel that a message ARRIVED. Every channel therefore grew its own way in — lane C's
/// Discord plugin PUTs turns over ARM as a service principal, which works and is exactly the shape that does
/// not generalise: the second door copies the first, and the copy drifts.</para>
///
/// <para><b>Two directions, two implementers, one contract each.</b> A <see cref="IInboundChannel"/> is
/// implemented by the DOMAIN (AKOS: a message becomes a turn) and called by the channel adapter; a
/// <see cref="IChannelResponder"/> is implemented by the CHANNEL (it knows how to post into a room, a thread
/// or a chat) and called by the domain when it has something to say back. Neither interface names a message
/// body, a turn, a persona or a Discord snowflake: this file is in <c>Core.Abstractions</c>, which the plugins
/// reference, so every domain word that leaked in here would be a word every channel had to compile against.
/// <c>The_seam_is_domain_free</c> is the fact that keeps it that way.</para>
///
/// <para><b>Idempotency is the channel's to declare and the domain's to honour.</b>
/// <see cref="InboundMessage.DedupeKey"/> is the sender's own identity for the message (Discord's message id,
/// WhatsApp's message id, an X post id). Every channel redelivers — that is what a webhook is — and a
/// redelivery must map to the same answer rather than to a second turn, which is why
/// <see cref="ChannelDelivery.Redelivered"/> exists as a distinct outcome from a refusal: "I already have
/// this" and "I will not take this" are different answers, and a caller that conflates them retries forever
/// or drops a real message.</para>
/// </summary>
/// <param name="ChannelId">The plugin id that owns the door, matching
/// <see cref="IChannelResponder.ChannelId"/> so a reply routes back without a lookup table.</param>
/// <param name="ConversationId">Where it arrived: the room, the chat, the thread.</param>
/// <param name="ActorId">Who sent it, in the CHANNEL's vocabulary — an adapter writes <c>discord:&lt;id&gt;</c>
/// so the kernel records provenance it never has to parse.</param>
/// <param name="Text">The text as it arrived. Empty is a real value (an image-only message), which is why the
/// seam does not treat it as absent.</param>
/// <param name="At">When the channel says it was sent, not when this kernel read it.</param>
/// <param name="ReplyToId">The message this one answers when the channel says so — a Discord reply, a quoted
/// WhatsApp message — and <c>null</c> when it does not. That is not the same as "this starts a thread": a
/// channel that cannot say leaves it empty and the domain answers in the conversation.</param>
/// <param name="DedupeKey">The sender's own identity for the message (Discord's message id, WhatsApp's message
/// id, an X post id). Every channel redelivers — that is what a webhook is — and this is what lets the domain
/// answer a redelivery with the handle it already has instead of creating a second turn.</param>
/// <param name="Metadata">The channel's own bag (a username, a room name, a locale). Deliberately a dictionary
/// of strings and not a <c>JsonObject</c>: this assembly has no JSON in its channel contracts, and a bag of
/// strings is all any adapter has needed.</param>
public sealed record InboundMessage(
    string ChannelId,
    string ConversationId,
    string ActorId,
    string Text,
    DateTimeOffset At,
    string? ReplyToId = null,
    string? DedupeKey = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// The DOMAIN's door: something arrived from a channel and this decides what it becomes. Implemented by the
/// domain (AKOS turns a message into a turn), called by the adapter that owns the transport.
///
/// <para>Implementations must be idempotent on <see cref="InboundMessage.DedupeKey"/> when the channel sent
/// one, and must answer honestly when they cannot: a refusal carries a reason a human can read, never a
/// silence.</para>
/// </summary>
public interface IInboundChannel
{
    /// <summary>The channel this door accepts, matching <see cref="IChannelResponder.ChannelId"/> so a reply
    /// can be routed back without a lookup table.</summary>
    string ChannelId { get; }

    /// <summary>Take one message. Returns what became of it — accepted (with what it became), recognised as a
    /// redelivery, or refused with a reason.</summary>
    ValueTask<ChannelDelivery> AcceptAsync(InboundMessage message, CancellationToken ct);
}

/// <summary>
/// What became of an inbound message. Three states, and they are three on purpose:
/// <see cref="Accepted"/> (a new handle), <see cref="Redelivered"/> (the same handle the channel already has —
/// the channel is being redelivered to, which is normal and must not produce a second turn) and
/// <see cref="Refused"/> (a reason). A caller told only "false" would have to guess which of the last two it
/// was, and guessing is how a retry storm or a dropped message happens.
/// </summary>
public sealed record ChannelDelivery(bool Accepted, string? Handle, string? Reason, bool Duplicate = false)
{
    /// <summary>Taken, and this is what it became (<paramref name="handle"/> — a turn id, a thread id, an
    /// admission, whatever the domain calls it).</summary>
    public static ChannelDelivery Ok(string handle) => new(true, handle, null);

    /// <summary>The channel sent this before: the handle is the SAME one, and nothing was created twice.</summary>
    public static ChannelDelivery Redelivered(string handle) => new(true, handle, null, Duplicate: true);

    /// <summary>Not taken, with a reason a person can read. Never invented by the seam — the domain's own
    /// sentence, recorded as it stands.</summary>
    public static ChannelDelivery Refused(string reason) => new(false, null, reason);
}

/// <summary>
/// The CHANNEL's door for the other direction: the domain has something to say back, and the adapter knows how
/// to say it (a message, a thread, a chat, a card). Implemented by the channel plugin, called by the domain's
/// respond stage, resolved by <see cref="ChannelId"/>.
/// </summary>
public interface IChannelResponder
{
    /// <summary>The channel this responder posts into — the same id the adapter's
    /// <see cref="IInboundChannel.ChannelId"/> carries.</summary>
    string ChannelId { get; }

    /// <summary>Say it. <paramref name="reply"/> carries where it goes; a responder that cannot reach the
    /// channel THROWS, because a reply that was not delivered must not look like one that was.</summary>
    ValueTask RespondAsync(ChannelReply reply, CancellationToken ct);
}

/// <summary>
/// One reply on its way back. <paramref name="ConversationId"/> is where it arrived (the room, the chat) and
/// <paramref name="ReplyToId"/> is the message it answers, so a channel that supports threads can open one and
/// a channel that does not still knows what it is answering. <paramref name="AsThread"/> is the domain's
/// request, not a guarantee: an adapter that cannot thread says so by rendering the reply in the conversation,
/// and the honest reading of this record is "answer THIS message", never "post into this room".
/// </summary>
public sealed record ChannelReply(
    string ChannelId,
    string ConversationId,
    string Text,
    string? ReplyToId = null,
    bool AsThread = false,
    RenderLane Lanes = RenderLane.Output);
