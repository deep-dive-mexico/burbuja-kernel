using Burbuja.Core.Abstractions.Events;

namespace Burbuja.Core.Abstractions.Channel;

/// <summary>
/// One message addressed to a channel.
///
/// <para><b>M8/P0.4:</b> <paramref name="Kind"/> carries the notice's severity through to the channel.
/// It used to be dropped on the floor by <c>ChannelNotifier</c>, which meant a channel could not tell a
/// routine progress line from a battery at 8 %, and the one escalation that matters — "wake the owner" —
/// had no way to express itself. Defaulted to <see cref="NoticeKind.Info"/> so a non-notice message
/// (a rendered lane, a job line) is unaffected.</para>
/// </summary>
public sealed record ChannelMessage(
    string ChannelId,
    RenderLane Lanes,
    string Body,
    string? EventType,
    long? Seq,
    NoticeKind Kind = NoticeKind.Info);

public interface IChannel
{
    string Id { get; }
    ValueTask SendAsync(ChannelMessage message, CancellationToken ct);
}
