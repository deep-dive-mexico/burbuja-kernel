using Burbuja.Core.Abstractions.Channel;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Channel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Channel;

/// <summary>
/// M7.1e. The notifier is the kernel's only outbound path to a human, so the two ways it can fail are
/// both silent: a notice that reaches nobody, and one channel's bad day muting the others.
/// </summary>
public sealed class ChannelNotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingChannel(string id, bool throws = false) : IChannel
    {
        public string Id { get; } = id;

        public List<ChannelMessage> Received { get; } = [];

        public ValueTask SendAsync(ChannelMessage message, CancellationToken ct)
        {
            if (throws)
                throw new InvalidOperationException("planted: this channel is misconfigured");
            Received.Add(message);
            return default;
        }
    }

    [Fact]
    public async Task A_host_with_no_channel_configured_is_a_no_op_not_a_failure()
    {
        // A kernel that refused to boot because a human could not be reached would be a worse failure
        // than a message that went nowhere.
        var notifier = new ChannelNotifier([], NullLogger<ChannelNotifier>.Instance);
        notifier.ChannelCount.ShouldBe(0);

        await notifier.NotifyAsync(new Notice(NoticeKind.Info, "hola"), Ct);
    }

    [Fact]
    public async Task Every_registered_channel_gets_the_notice_on_the_monitor_lane()
    {
        var a = new RecordingChannel("discord");
        var b = new RecordingChannel("slack");
        var notifier = new ChannelNotifier([a, b], NullLogger<ChannelNotifier>.Instance);

        await notifier.NotifyAsync(new Notice(NoticeKind.Alert, "🧠 Presión de memoria al 12%."), Ct);

        foreach (var channel in new[] { a, b })
        {
            var message = channel.Received.ShouldHaveSingleItem();
            message.ChannelId.ShouldBe(channel.Id, "the channel is addressed by its own id");
            message.Body.ShouldBe("🧠 Presión de memoria al 12%.");
            message.Lanes.ShouldBe(RenderLane.Monitor);
            message.EventType.ShouldBe("notice");
        }
    }

    [Fact]
    public async Task One_misconfigured_channel_does_not_silence_the_others()
    {
        var broken = new RecordingChannel("broken", throws: true);
        var working = new RecordingChannel("working");
        var notifier = new ChannelNotifier([broken, working], NullLogger<ChannelNotifier>.Instance);

        await notifier.NotifyAsync(new Notice(NoticeKind.Warn, "💾 Quedan 4 GB libres."), Ct);

        working.Received.ShouldHaveSingleItem("a channel that throws must not stop the fan-out");
    }

    [Fact]
    public async Task The_notifier_does_not_deduplicate_because_it_cannot_know_what_is_new()
    {
        // Edge-triggering needs the condition, and the condition lives with whoever evaluates it
        // (HostFactsPublisher). A notifier that suppressed repeats on its own would be guessing.
        var channel = new RecordingChannel("discord");
        var notifier = new ChannelNotifier([channel], NullLogger<ChannelNotifier>.Instance);

        await notifier.NotifyAsync(new Notice(NoticeKind.Info, "same"), Ct);
        await notifier.NotifyAsync(new Notice(NoticeKind.Info, "same"), Ct);

        channel.Received.Count.ShouldBe(2);
    }
}
