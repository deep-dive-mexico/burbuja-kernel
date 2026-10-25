using System.Collections.ObjectModel;
using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Fleet;

/// <summary>Node-owned bounded claims. It never calls a lobby or waits for network I/O.</summary>
internal sealed class SessionResources
{
    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<string, int> _limits;
    private readonly Dictionary<(string Owner, string Resource), int> _claims = [];
    private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<ISessionResourceAdmission> _admission;

    public SessionResources(IReadOnlyDictionary<string, int> limits, IEnumerable<ISessionResourceAdmission>? admission = null)
    {
        if (limits.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 64 || p.Value < 0))
            throw new ArgumentException("Realtime:Resources requires bounded names and nonnegative limits.", nameof(limits));
        _limits = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(limits, StringComparer.Ordinal));
        _admission = admission?.ToArray() ?? [];
    }

    public IReadOnlyDictionary<string, int> Limits => _limits;
    public IReadOnlyDictionary<string, int> Available
    {
        get { lock (_gate) return _limits.ToDictionary(p => p.Key,
            p => _admission.All(a => a.CanIncrease(p.Key)) ? p.Value - _used.GetValueOrDefault(p.Key) : 0, StringComparer.Ordinal); }
    }

    public bool TrySet(string owner, string resource, int units, bool allowIncrease = true)
    {
        if (units < 0 || !_limits.TryGetValue(resource, out int maximum)) return false;
        lock (_gate)
        {
            var key = (owner, resource);
            int previous = _claims.GetValueOrDefault(key);
            if (units > previous && (!allowIncrease || _admission.Any(a => !a.CanIncrease(resource)))) return false;
            long next = (long)_used.GetValueOrDefault(resource) - previous + units;
            if (next > maximum) return false;
            _used[resource] = (int)next;
            if (units == 0) _claims.Remove(key); else _claims[key] = units;
            return true;
        }
    }

    public void Release(string owner)
    {
        lock (_gate)
            foreach (var key in _claims.Keys.Where(k => k.Owner == owner).ToArray())
            { _used[key.Resource] -= _claims[key]; _claims.Remove(key); }
    }
}
