using System.Net.WebSockets;
using System.Text;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Transport;
using Xunit;

namespace Burbuja.Realtime.Tests;

public sealed class OutboundDeliveryTests
{
    [Fact]
    public async Task Fresh_snapshots_cannot_evict_the_stage_they_depend_on()
    {
        using var socket = new RecordingSocket();
        IPlayerConnection connection = new WebSocketPlayerConnection(socket, 2);
        Assert.True(connection.TrySend(Encoding.UTF8.GetBytes("stage"), FrameFormat.Text, FrameDelivery.Reliable));
        for (int i = 0; i < 20; i++)
            Assert.True(connection.TrySend(Encoding.UTF8.GetBytes($"state:{i}"), FrameFormat.Text, FrameDelivery.Latest, "state"));
        connection.Close(RealtimeCloseCode.Normal, "test complete");
        await ((WebSocketPlayerConnection)connection).RunAsync(new Inbox(), 1024, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "stage", "state:19" }, socket.Sent);
    }

    [Fact]
    public async Task Replacement_state_stays_after_new_control_barriers()
    {
        using var socket = new RecordingSocket();
        IPlayerConnection connection = new WebSocketPlayerConnection(socket, 3);
        connection.TrySend(Encoding.UTF8.GetBytes("old-state"), FrameFormat.Text, FrameDelivery.Latest, "state");
        connection.TrySend(Encoding.UTF8.GetBytes("new-stage"), FrameFormat.Text, FrameDelivery.Reliable);
        connection.TrySend(Encoding.UTF8.GetBytes("new-state"), FrameFormat.Text, FrameDelivery.Latest, "state");
        connection.Close(RealtimeCloseCode.Normal, "done");
        await ((WebSocketPlayerConnection)connection).RunAsync(new Inbox(), 1024, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "new-stage", "new-state" }, socket.Sent);
    }

    [Fact]
    public async Task Reliable_overflow_fails_explicitly_and_retains_already_accepted_controls()
    {
        using var socket = new RecordingSocket();
        IPlayerConnection connection = new WebSocketPlayerConnection(socket, 2);
        Assert.True(connection.TrySend(Encoding.UTF8.GetBytes("stage"), FrameFormat.Text, FrameDelivery.Reliable));
        Assert.True(connection.TrySend(Encoding.UTF8.GetBytes("result"), FrameFormat.Text, FrameDelivery.Reliable));
        Assert.True(connection.TrySend(Encoding.UTF8.GetBytes("state"), FrameFormat.Text, FrameDelivery.Latest, "state"));
        Assert.False(connection.TrySend(Encoding.UTF8.GetBytes("next-stage"), FrameFormat.Text, FrameDelivery.Reliable));
        await ((WebSocketPlayerConnection)connection).RunAsync(new Inbox(), 1024, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "stage", "result" }, socket.Sent);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.SentClose);
    }

    [Fact]
    public async Task Dropping_an_oversized_snapshot_does_not_spin_or_block_later_control()
    {
        using var socket = new RecordingSocket();
        IPlayerConnection connection = new WebSocketPlayerConnection(socket, 2);
        connection.TrySend(Encoding.UTF8.GetBytes("old-state"), FrameFormat.Text, FrameDelivery.Latest, "state");
        Assert.True(connection.TrySend(new byte[1024 * 1024 + 1], FrameFormat.Binary, FrameDelivery.Latest, "state"));
        connection.TrySend(Encoding.UTF8.GetBytes("stage"), FrameFormat.Text, FrameDelivery.Reliable);
        connection.Close(RealtimeCloseCode.Normal, "done");
        await ((WebSocketPlayerConnection)connection).RunAsync(new Inbox(), 1024, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "stage" }, socket.Sent);
    }

    private sealed class Inbox : IConnectionInbox
    {
        public void Receive(ReadOnlySpan<byte> frame, FrameFormat format) { }
        public void Disconnected() { }
    }
    private sealed class RecordingSocket : WebSocket
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        public List<string> Sent { get; } = [];
        public WebSocketCloseStatus? SentClose { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "closed";
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _closed.TrySetResult(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => CloseOutputAsync(closeStatus, statusDescription, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
        { SentClose = closeStatus; _state = WebSocketState.Closed; _closed.TrySetResult(); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        { await _closed.Task.WaitAsync(ct); return new(0, WebSocketMessageType.Close, true); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        { Sent.Add(Encoding.UTF8.GetString(buffer)); return Task.CompletedTask; }
    }
}
