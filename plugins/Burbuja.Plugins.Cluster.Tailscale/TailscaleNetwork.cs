using System.Net;
using System.Text.Json;
using Burbuja.Cluster.Abstractions;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Cluster.Tailscale;

public sealed class TailscalePlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Cluster.Tailscale",
        "Cluster network (Tailscale)",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Cluster.Tailscale.dll",
        typeof(TailscalePlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["cluster.network"],
        [],
        // Reads the client's status through its command-line tool. It never changes the tailnet.
        [PluginCapability.ProcessExecution],
        ["all", "control-plane", "worker"],
        "Cluster:Tailscale",
        false,
        [],
        345);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INetworkProvider>(sp => new TailscaleNetwork(
            sp.GetRequiredService<IConfiguration>().GetSection("Cluster:Tailscale").Get<TailscaleOptions>() ?? new TailscaleOptions()));
    }
}

public sealed class TailscaleOptions
{
    /// <summary>Path of the <c>tailscale</c> tool. Empty searches the usual locations.</summary>
    public string Executable { get; set; } = "";

    /// <summary>
    /// Addresses to advertise instead of the derived ones, for a node published through <c>tailscale serve</c>
    /// (HTTPS on the MagicDNS name, usually on another port than the node listens on).
    /// </summary>
    public List<string> Advertise { get; set; } = [];
}

/// <summary>What <c>tailscale status --json</c> says, reduced to what a cluster needs.</summary>
public sealed record TailscaleStatus(string BackendState, string? DnsName, IReadOnlyList<string> Addresses, IReadOnlyList<string> PeerAddresses, string? Suffix)
{
    public bool Running => BackendState == "Running";

    /// <summary>Reads the tool's JSON. Returns null for anything that is not that document.</summary>
    public static TailscaleStatus? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("BackendState", out var state) || state.ValueKind != JsonValueKind.String)
                return null;

            string? dnsName = null;
            var addresses = new List<string>();
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                // The tool prints the name fully qualified, with the root dot.
                if (self.TryGetProperty("DNSName", out var name) && name.ValueKind == JsonValueKind.String)
                    dnsName = name.GetString()!.TrimEnd('.') is { Length: > 0 } trimmed ? trimmed : null;
                addresses.AddRange(Addresses(self));
            }

            var peers = new List<string>();
            if (root.TryGetProperty("Peer", out var peer) && peer.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in peer.EnumerateObject()) peers.AddRange(Addresses(entry.Value));
            }

            var suffix = root.TryGetProperty("MagicDNSSuffix", out var magic) && magic.ValueKind == JsonValueKind.String ? magic.GetString() : null;
            return new TailscaleStatus(state.GetString()!, dnsName, addresses, peers, suffix is { Length: > 0 } ? suffix : null);
        }
        catch (JsonException)
        {
            return null;
        }

        static IEnumerable<string> Addresses(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (var ip in ips.EnumerateArray())
            {
                if (ip.ValueKind == JsonValueKind.String && IPAddress.TryParse(ip.GetString(), out _)) yield return ip.GetString()!;
            }
        }
    }
}

/// <summary>
/// Tailscale as a cluster network.
///
/// <para><b>Membership decides, not the address range.</b> A tailnet address lives in <c>100.64.0.0/10</c>,
/// which other overlays also use, so an address is claimed only when it is this node's, one of its peers', or
/// a name under the tailnet's own DNS suffix. An address in that range that the tailnet does not know is left
/// for whoever does.</para>
///
/// <para><b>Read-only.</b> The provider runs <c>tailscale status --json</c> and nothing else. It does not log
/// in, change preferences, or publish anything with <c>tailscale serve</c>; when a node is published that way
/// the operator states the resulting address in <see cref="TailscaleOptions.Advertise"/>. On macOS the app's
/// executable only behaves as a command-line tool when <c>TAILSCALE_BE_CLI=1</c> is set, which this provider
/// sets for its own subprocess alone.</para>
/// </summary>
public sealed class TailscaleNetwork(TailscaleOptions options) : INetworkProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, string> CliEnvironment = new() { ["TAILSCALE_BE_CLI"] = "1" };
    private readonly Lock _gate = new();
    private TailscaleStatus? _status;
    private string _problem = "not-read";
    private DateTimeOffset _readAt;

    public string Id => ClusterConstants.Networks.Tailscale;

    private string? Tool => options.Executable.Length > 0
        ? options.Executable
        : ProbeCommand.Find(
            "/usr/bin/tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale",
            "/Applications/Tailscale.app/Contents/MacOS/Tailscale");

    private async ValueTask<TailscaleStatus?> ReadAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _readAt < CacheFor) return _status;
        }

        TailscaleStatus? status = null;
        string problem;
        if (Tool is not { } tool)
        {
            problem = "not-installed";
        }
        else
        {
            var json = await ProbeCommand.ReadAsync(tool, ["status", "--json"], TimeSpan.FromSeconds(3.5), ct, CliEnvironment);
            status = json is null ? null : TailscaleStatus.Parse(json);
            problem = status is null ? "unreadable" : status.Running ? "" : status.BackendState switch
            {
                "NeedsLogin" => "needs-login",
                "Stopped" => "stopped",
                _ => "not-running",
            };
        }

        lock (_gate)
        {
            _status = status;
            _problem = problem;
            _readAt = DateTimeOffset.UtcNow;
        }

        return status;
    }

    public async ValueTask<NetworkStatus> DetectAsync(CancellationToken ct)
    {
        var status = await ReadAsync(ct);
        if (status is { Running: true })
            return new NetworkStatus(Id, EndpointStatus.Available, Addresses: status.Addresses, HostName: status.DnsName);

        // A tool that could not be asked is "unknown", never "down": only a real answer says the tailnet is off.
        return new NetworkStatus(Id, status is null ? EndpointStatus.Unknown : EndpointStatus.Unavailable, _problem);
    }

    public async ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct)
    {
        var status = await ReadAsync(ct);
        if (status is not { Running: true }) return [];
        if (options.Advertise.Count > 0)
        {
            return [.. options.Advertise
                .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null)
                .OfType<Uri>()
                .Select(url => new AdvertisedEndpoint(Id, url, EndpointReachability.PrivateNetwork))];
        }

        var endpoints = new List<AdvertisedEndpoint>();
        if (status.DnsName is { } name)
            endpoints.Add(new AdvertisedEndpoint(Id, new Uri($"{listener.Scheme}://{name}:{listener.Port}"), EndpointReachability.PrivateNetwork));
        foreach (var address in status.Addresses.Where(a => IPAddress.Parse(a).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            endpoints.Add(new AdvertisedEndpoint(Id, new Uri($"{listener.Scheme}://{address}:{listener.Port}"), EndpointReachability.PrivateNetwork));
        return endpoints;
    }

    public EndpointReachability? Classify(Uri url)
    {
        TailscaleStatus? status;
        lock (_gate) status = _status;
        var host = url.Host.TrimEnd('.');

        // Once this machine's tailnet is known, only names under its own suffix are its members. Until then
        // the vendor's domain is the best available evidence that a name is some tailnet's.
        if (status?.Suffix is { } suffix)
        {
            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return EndpointReachability.PrivateNetwork;
        }
        else if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointReachability.PrivateNetwork;
        }

        if (status is null) return null;
        if (!IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip)) return null;
        var text = ip.ToString();
        return status.Addresses.Contains(text) || status.PeerAddresses.Contains(text) ? EndpointReachability.PrivateNetwork : null;
    }
}
