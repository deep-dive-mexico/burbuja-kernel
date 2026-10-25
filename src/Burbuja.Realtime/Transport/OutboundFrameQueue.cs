using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Transport;

internal sealed class OutboundFrameQueue(int capacity)
{
    internal sealed record Frame(ReadOnlyMemory<byte> Bytes, FrameFormat Format, FrameDelivery? Delivery, string? Key);
    private const int MaximumBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly LinkedList<Frame> _frames = [];
    private TaskCompletionSource _ready = Signal();
    private long _bytes;
    private bool _completed;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryWrite(ReadOnlyMemory<byte> bytes, FrameFormat format, FrameDelivery? delivery = null, string? key = null)
    {
        if (delivery == FrameDelivery.Latest && string.IsNullOrEmpty(key)) throw new ArgumentException("Latest delivery needs a replacement key.", nameof(key));
        lock (_gate)
        {
            if (_completed) return false;
            if (delivery == FrameDelivery.Latest)
                for (var node = _frames.First; node is not null; node = node.Next)
                    if (node.Value.Delivery == delivery && node.Value.Key == key) { Remove(node); break; }
            while (_frames.Count >= capacity || delivery is not null && _bytes + bytes.Length > MaximumBytes)
            {
                var replaceable = _frames.First;
                if (delivery is not null)
                    while (replaceable is not null && replaceable.Value.Delivery != FrameDelivery.Latest) replaceable = replaceable.Next;
                if (replaceable is null)
                {
                    if (_frames.Count == 0 && _ready.Task.IsCompleted) _ready = Signal();
                    return delivery == FrameDelivery.Latest;
                }
                Remove(replaceable);
            }
            _frames.AddLast(new Frame(bytes, format, delivery, key)); _bytes += bytes.Length;
            _ready.TrySetResult(); return true;
        }
    }

    public async ValueTask<Frame?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            Task ready;
            lock (_gate)
            {
                if (_frames.First is { } node)
                {
                    var result = node.Value; Remove(node);
                    if (_frames.Count == 0 && !_completed) _ready = Signal();
                    return result;
                }
                if (_completed) return null;
                ready = _ready.Task;
            }
            await ready.WaitAsync(ct);
        }
    }
    public void Complete() { lock (_gate) { _completed = true; _ready.TrySetResult(); } }
    private void Remove(LinkedListNode<Frame> node) { _bytes -= node.Value.Bytes.Length; _frames.Remove(node); }
}
