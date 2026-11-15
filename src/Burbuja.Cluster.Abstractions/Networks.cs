namespace Burbuja.Cluster.Abstractions;

/// <summary>
/// How far an address reaches, ordered by preference: a node tries the nearest route that answers. A tunnel
/// is last because it needs setup before it can be dialled at all.
/// </summary>
public enum EndpointReachability
{
    Loopback = 0,
    Lan = 1,
    PrivateNetwork = 2,
    Public = 3,
    Tunnel = 4,
}

public enum EndpointStatus
{
    Unknown,
    Available,
    Unavailable,
}

/// <summary>
/// One address a node believes it can be reached at, and which network supplied it. The network is recorded
/// rather than inferred from the address: carrier-grade NAT space (<c>100.64.0.0/10</c>) is shared by several
/// overlay networks, so the same address can belong to different ones on different machines.
/// </summary>
public sealed record AdvertisedEndpoint(
    string Network,
    Uri Url,
    EndpointReachability Reachability,
    EndpointStatus Status = EndpointStatus.Unknown,
    string? Detail = null);

/// <summary>What a provider found on this machine. <paramref name="Detail"/> is a fixed label, never raw tool output.</summary>
public sealed record NetworkStatus(
    string Network,
    EndpointStatus Status,
    string? Detail = null,
    IReadOnlyList<string>? Addresses = null,
    string? HostName = null);

/// <summary>The port this node listens on and the scheme it serves there.</summary>
public sealed record ListenerBinding(string Scheme, int Port);

/// <summary>A candidate way to reach a peer. <paramref name="Learned"/> routes came from the peer itself and expire with it.</summary>
public sealed record NodeRoute(string Network, Uri Url, EndpointReachability Reachability, bool Learned = false);

/// <summary>
/// A route made ready to dial. For a direct network <see cref="DialUrl"/> is the route's own address; for a
/// mechanism that needs setup it is whatever the setup produced (a forwarded loopback port). Disposing it
/// tears down only what the provider itself created.
/// </summary>
public sealed class PreparedRoute(NodeRoute route, Uri dialUrl, Func<ValueTask>? release = null) : IAsyncDisposable
{
    public NodeRoute Route { get; } = route;
    public Uri DialUrl { get; } = dialUrl;

    public ValueTask DisposeAsync() => release?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>
/// One network a node can be reached over or reach others through: the loopback, a LAN, an overlay such as
/// Tailscale or Twingate, an operator-supplied address.
///
/// <para><b>A provider describes; it does not decide.</b> It reports whether its network is usable here, which
/// addresses it gives this node, and whether a given address is one of its own. Choosing between routes,
/// probing them and retrying belong to the single connection owner in the runtime, so a second network never
/// brings a second retry policy with it.</para>
///
/// <para><b>Nothing here changes the network.</b> A provider reads state. One that must create something to be
/// usable (a forwarded port) does so in <see cref="PrepareAsync"/> and removes exactly that on disposal.</para>
/// </summary>
public interface INetworkProvider
{
    string Id { get; }

    ValueTask<NetworkStatus> DetectAsync(CancellationToken ct);

    /// <summary>The addresses this network gives the local listener. Empty when the network is not usable here.</summary>
    ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct);

    /// <summary>How far <paramref name="url"/> reaches if it belongs to this network; null when it does not.</summary>
    EndpointReachability? Classify(Uri url);

    ValueTask<PreparedRoute> PrepareAsync(NodeRoute route, CancellationToken ct) =>
        ValueTask.FromResult(new PreparedRoute(route, route.Url));
}
