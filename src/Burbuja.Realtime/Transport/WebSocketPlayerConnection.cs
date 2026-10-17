using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Transport;

/// <summary>
/// The WebSocket transport for one admitted player. Outbound frames go through a bounded buffer that drops
/// its OLDEST frame when full: a client that cannot keep up gets the newest state instead of stalling the
/// lobby or growing the server's memory. Inbound frames, text or binary, are reassembled up to the game's bound
/// and handed to the lobby's inbox; a larger frame closes the connection before it is buffered.
/// </summary>
internal sealed class WebSocketPlayerConnection : IPlayerConnection
{
    private static readonly TimeSpan CloseHandshake = TimeSpan.FromSeconds(5);

    /// <summary>RFC 6455 allows 123 bytes of close reason.</summary>
    private const int MaxCloseReasonBytes = 123;

    private readonly WebSocket _socket;
    private readonly Channel<(ReadOnlyMemory<byte> Frame, FrameFormat Format)> _outbound;
    private int _closing;
    private RealtimeCloseCode _closeCode = RealtimeCloseCode.Normal;
    private string _closeReason = "";

    public WebSocketPlayerConnection(WebSocket socket, int sendBufferFrames)
    {
        _socket = socket;
        _outbound = Channel.CreateBounded<(ReadOnlyMemory<byte>, FrameFormat)>(new BoundedChannelOptions(sendBufferFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

    public bool TrySend(ReadOnlyMemory<byte> frame, FrameFormat format) =>
        Volatile.Read(ref _closing) == 0 && _outbound.Writer.TryWrite((frame, format));

    public void Close(RealtimeCloseCode code, string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
            return;
        _closeCode = code;
        _closeReason = CloseReason(reason);
        _outbound.Writer.TryComplete();
    }

    /// <summary>The reason cut to what a close frame carries, by UTF-8 bytes and never inside a character: a
    /// game's kick reason may be any text, and an oversized one would make the close itself throw.</summary>
    internal static string CloseReason(string reason)
    {
        if (Encoding.UTF8.GetByteCount(reason) <= MaxCloseReasonBytes)
            return reason;
        var length = 0;
        var bytes = 0;
        foreach (var rune in reason.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxCloseReasonBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }

        return reason[..length];
    }

    /// <summary>Runs both directions until the socket is done. Returns once the close handshake finished or
    /// timed out; the inbox has been told in either case.</summary>
    public async Task RunAsync(IConnectionInbox inbox, int maxMessageBytes, CancellationToken ct)
    {
        var reader = ReadGuardedAsync(inbox, maxMessageBytes, ct);

        // The writer ends once either side has closed and the close frame is out. A peer that never answers
        // that frame would hold the reader (and this request) forever, so it gets a bounded handshake.
        await WriteAsync(ct);
        if (await Task.WhenAny(reader, Task.Delay(CloseHandshake, CancellationToken.None)) != reader)
            _socket.Abort();
        await reader;
        if (_socket.State != WebSocketState.Closed)
            _socket.Abort();
    }

    private async Task ReadGuardedAsync(IConnectionInbox inbox, int maxMessageBytes, CancellationToken ct)
    {
        try
        {
            await ReadAsync(inbox, maxMessageBytes, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            inbox.Disconnected();
            Close(RealtimeCloseCode.Normal, "closed");
        }
    }

    private async Task ReadAsync(IConnectionInbox inbox, int maxMessageBytes, CancellationToken ct)
    {
        var buffer = new byte[maxMessageBytes];
        while (_socket.State == WebSocketState.Open)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length)
                {
                    Close(RealtimeCloseCode.MessageTooBig, "message_too_big");
                    return;
                }

                result = await _socket.ReceiveAsync(buffer.AsMemory(count), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                count += result.Count;
            }
            while (!result.EndOfMessage);

            inbox.Receive(buffer.AsSpan(0, count),
                result.MessageType == WebSocketMessageType.Binary ? FrameFormat.Binary : FrameFormat.Text);
        }
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (frame, format) in _outbound.Reader.ReadAllAsync(ct))
                await _socket.SendAsync(frame,
                    format == FrameFormat.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct);
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(CloseHandshake);
                await _socket.CloseOutputAsync((WebSocketCloseStatus)_closeCode, _closeReason, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ChannelClosedException)
        {
        }
    }
}
