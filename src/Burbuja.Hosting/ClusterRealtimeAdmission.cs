using Burbuja.Cluster.Abstractions;
using Burbuja.Realtime.Abstractions;

namespace Burbuja.Hosting;

/// <summary>
/// Lets a cluster member's own pressure verdict hold back the realtime module on the same host
/// (<c>Cluster:Agent:GuardRealtime</c>).
///
/// <para>While the node is under pressure no session may claim more node resources; claims already held are
/// untouched, so running matches are never interrupted. An unknown verdict admits: realtime work is work
/// somebody is waiting for, and a node that cannot read itself must not turn that into an outage. It reads
/// one cached snapshot, as the contract for the game loop requires.</para>
/// </summary>
internal sealed class ClusterRealtimeAdmission(INodePressure pressure) : ISessionResourceAdmission
{
    public bool CanIncrease(string resource)
    {
        var local = pressure.Local;
        return !(local.Known && local.Pressured);
    }
}
