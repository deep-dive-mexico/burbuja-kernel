using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

/// <summary>
/// The ring itself. A fixed-size queue under a lock: the writes are one line of work at the end of a
/// request, and the reads are a snapshot for a portal page or a CLI call.
///
/// <para><b>Bounded on purpose, and the bound is reported.</b> An unbounded list is a memory leak with a
/// friendly name, and a silent cap is worse than either: a reader who cannot tell a full ring from a quiet
/// hour will draw the wrong conclusion from the same screen.</para>
/// </summary>
public sealed class InMemoryArmActivityLog : IArmActivityLog
{
    public const int DefaultCapacity = 1000;

    private readonly Lock _gate = new();
    private readonly Queue<ArmActivityRow> _rows;
    private long _total;

    public InMemoryArmActivityLog(int capacity = DefaultCapacity)
    {
        Capacity = capacity > 0 ? capacity : DefaultCapacity;
        _rows = new Queue<ArmActivityRow>(Capacity);
    }

    public int Capacity { get; }

    public long Total
    {
        get
        {
            lock (_gate)
                return _total;
        }
    }

    public void Record(ArmActivityRow row)
    {
        lock (_gate)
        {
            _rows.Enqueue(row);
            _total++;
            while (_rows.Count > Capacity)
                _rows.Dequeue();
        }
    }

    public IReadOnlyList<ArmActivityRow> Recent(string? principalId = null, DateTimeOffset? since = null, int limit = 200)
    {
        var take = limit <= 0 ? 200 : limit;
        lock (_gate)
        {
            IEnumerable<ArmActivityRow> rows = _rows;
            if (principalId is { Length: > 0 })
                rows = rows.Where(r => string.Equals(r.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase));
            if (since is { } at)
                rows = rows.Where(r => r.At >= at);

            // Newest LAST in the ring, and the newest are the ones a reader wants: take from the tail.
            return [.. rows.Reverse().Take(take).Reverse()];
        }
    }
}
