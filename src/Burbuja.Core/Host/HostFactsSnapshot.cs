using Burbuja.Core.Abstractions.Host;

namespace Burbuja.Core.Host;

/// <summary>
/// M7.1d: the default <see cref="IHostFactsSnapshot"/> — one reference, swapped atomically and read
/// through a volatile field, so a reader on any thread sees either the previous reading or the new
/// one and never a torn record. There is no lock and no history: the snapshot answers "what did the
/// last collection see", and every consumer that needs chronology (the notifier, the portal spark
/// line) keeps its own.
/// </summary>
public sealed class HostFactsSnapshot : IHostFactsSnapshot
{
    private volatile HostFacts? _latest;

    public HostFacts? Latest => _latest;

    public void Publish(HostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _latest = facts;
    }
}
