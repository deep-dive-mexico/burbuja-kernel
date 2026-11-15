using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster.Networks;

/// <summary>Address arithmetic shared by the built-in networks and the fallback classification.</summary>
internal static class AddressRanges
{
    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>RFC 1918 space: addresses that mean "this site" and nothing else.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// Carrier-grade NAT space, <c>100.64.0.0/10</c>. Several overlay networks hand out addresses from it, so
    /// an address here says "some overlay" and never which one; only the overlay's own provider can tell.
    /// </summary>
    public static bool IsSharedOverlay(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    /// <summary>What an address reaches when no provider claimed it.</summary>
    public static EndpointReachability Fallback(Uri url)
    {
        if (IsLoopbackHost(url.Host)) return EndpointReachability.Loopback;
        if (!IPAddress.TryParse(url.Host, out var ip)) return EndpointReachability.Public;
        if (IsPrivate(ip)) return EndpointReachability.Lan;
        return IsSharedOverlay(ip) ? EndpointReachability.PrivateNetwork : EndpointReachability.Public;
    }
}

internal sealed class LoopbackNetwork : INetworkProvider
{
    public string Id => ClusterConstants.Networks.Loopback;

    public ValueTask<NetworkStatus> DetectAsync(CancellationToken ct) =>
        ValueTask.FromResult(new NetworkStatus(Id, EndpointStatus.Available, Addresses: ["127.0.0.1"]));

    public ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AdvertisedEndpoint>>(
            [new(Id, new Uri($"{listener.Scheme}://127.0.0.1:{listener.Port}"), EndpointReachability.Loopback, EndpointStatus.Available)]);

    public EndpointReachability? Classify(Uri url) => AddressRanges.IsLoopbackHost(url.Host) ? EndpointReachability.Loopback : null;
}

/// <summary>
/// The site network: private IPv4 addresses on interfaces that are up. Point-to-point and tunnel interfaces
/// are left to the overlay that owns them, so an overlay address is never advertised as a LAN address.
/// </summary>
internal sealed class LanNetwork : INetworkProvider
{
    public string Id => ClusterConstants.Networks.Lan;

    public ValueTask<NetworkStatus> DetectAsync(CancellationToken ct)
    {
        var addresses = Addresses();
        return ValueTask.FromResult(new NetworkStatus(
            Id,
            addresses.Count > 0 ? EndpointStatus.Available : EndpointStatus.Unavailable,
            addresses.Count > 0 ? null : "no private address on an active interface",
            addresses));
    }

    public ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AdvertisedEndpoint>>(
            [.. Addresses().Select(a => new AdvertisedEndpoint(Id, new Uri($"{listener.Scheme}://{a}:{listener.Port}"), EndpointReachability.Lan))]);

    public EndpointReachability? Classify(Uri url) =>
        IPAddress.TryParse(url.Host, out var ip) && AddressRanges.IsPrivate(ip) ? EndpointReachability.Lan : null;

    private static List<string> Addresses()
    {
        var found = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (AddressRanges.IsPrivate(unicast.Address)) found.Add(unicast.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Interface enumeration can be denied in a sandbox; that is "no LAN known", not a failure.
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}

/// <summary>
/// Addresses the operator states outright, for a network that has no provider: a reverse proxy, a port
/// forward, a name on a VPN the kernel does not know. Nothing is detected; the operator's word is advertised.
/// </summary>
internal sealed class StaticNetwork(ClusterOptions options) : INetworkProvider
{
    public string Id => ClusterConstants.Networks.Static;

    public ValueTask<NetworkStatus> DetectAsync(CancellationToken ct) =>
        ValueTask.FromResult(new NetworkStatus(Id, Urls().Count > 0 ? EndpointStatus.Available : EndpointStatus.Unavailable, Addresses: [.. Urls().Select(u => u.ToString())]));

    public ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AdvertisedEndpoint>>(
            [.. Urls().Select(u => new AdvertisedEndpoint(Id, u, AddressRanges.Fallback(u)))]);

    public EndpointReachability? Classify(Uri url) => null;

    private List<Uri> Urls() =>
        [.. options.Advertise.Select(a => Uri.TryCreate(a, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null).OfType<Uri>()];
}
