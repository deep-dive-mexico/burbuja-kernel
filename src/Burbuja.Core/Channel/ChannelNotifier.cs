using Burbuja.Core.Abstractions.Channel;
using Burbuja.Core.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Burbuja.Core.Channel;

/// <summary>
/// M7.1e: the kernel's <see cref="INotifier"/> — it fans a notice out to every registered
/// <see cref="IChannel"/>. Nothing in the kernel knows what a Discord is.
///
/// <para>Zero channels is not a failure: a host with no channel configured still runs and its notices
/// are simply addressed to nobody. That is why this is a no-op rather than a throw — refusing to boot
/// because a human cannot be reached is a worse failure than a missing message.</para>
///
/// <para>A channel that throws does not stop the others. Notices are best-effort by definition, and
/// one misconfigured channel must not silence the rest.</para>
///
/// <para>There is no deduplication here on purpose: only the component evaluating a condition knows
/// when it is new, so edge-triggering belongs there (see <c>HostFactsPublisher</c>) and this stays a
/// pure fan-out.</para>
/// </summary>
public sealed class ChannelNotifier(
    IEnumerable<IChannel> channels,
    ILogger<ChannelNotifier> log) : INotifier
{
    private readonly IReadOnlyList<IChannel> _channels = channels.ToArray();

    public int ChannelCount => _channels.Count;

    public async ValueTask NotifyAsync(Notice notice, CancellationToken ct)
    {
        if (_channels.Count == 0)
        {
            log.LogDebug("notice with no channel registered: {Text}", notice.Text);
            return;
        }

        foreach (var channel in _channels)
        {
            try
            {
                await channel.SendAsync(
                    new ChannelMessage(
                        ChannelId: channel.Id,
                        Lanes: RenderLane.Monitor,
                        Body: notice.Text,
                        EventType: "notice",
                        Seq: null,
                        // P0.4: severity reaches the channel. Dropping it here is why a chat surface
                        // could only ever render every notice the same way, and why "this one should
                        // reach a phone" had nowhere to live.
                        Kind: notice.Kind),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One channel's bad day is not another channel's problem.
                log.LogError(ex, "channel '{ChannelId}' failed to deliver a notice", channel.Id);
            }
        }
    }
}
