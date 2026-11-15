using Burbuja.Cluster.Abstractions;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Cluster.Twingate;

public sealed class TwingatePlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Cluster.Twingate",
        "Cluster network (Twingate)",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Cluster.Twingate.dll",
        typeof(TwingatePlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["cluster.network"],
        [],
        // Reads the client's state through its command-line tool where one exists. It never changes access.
        [PluginCapability.ProcessExecution],
        ["all", "control-plane", "worker"],
        "Cluster:Twingate",
        false,
        [],
        345);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INetworkProvider>(sp => new TwingateNetwork(
            sp.GetRequiredService<IConfiguration>().GetSection("Cluster:Twingate").Get<TwingateOptions>() ?? new TwingateOptions()));
    }
}

public sealed class TwingateOptions
{
    /// <summary>Path of the <c>twingate</c> client tool. Empty searches the usual locations.</summary>
    public string Executable { get; set; } = "";

    /// <summary>
    /// The addresses under which this node is published as a Twingate Resource, exactly as defined in the
    /// Twingate admin console (an alias and a port), e.g. <c>http://node-a.corp.internal:18520</c>.
    /// </summary>
    public List<string> Advertise { get; set; } = [];

    /// <summary>DNS suffixes of Resource aliases, e.g. <c>corp.internal</c>. Names under them are reached through Twingate.</summary>
    public List<string> Suffixes { get; set; } = [];
}

/// <summary>
/// Twingate as a cluster network.
///
/// <para><b>Twingate is not a mesh, and this provider does not pretend it is.</b> A machine is reachable
/// through Twingate because an administrator defined it as a Resource behind a Connector, not because the
/// machine runs a client; the alias and its access policy live in the Twingate console. So nothing about a
/// node's own reachability can be discovered locally: the operator states the Resource addresses in
/// <see cref="TwingateOptions.Advertise"/>, and they are advertised as hints like any other address. The
/// client only matters in the other direction, for dialling a peer's alias, which is what
/// <see cref="DetectAsync"/> reports.</para>
///
/// <para><b>Names decide, not addresses.</b> A Twingate client answers DNS for Resource aliases with synthetic
/// addresses from carrier-grade NAT space, the range Tailscale also uses, and those addresses differ per
/// machine. An address is therefore claimed by its alias suffix only, never by its numeric range.</para>
///
/// <para>The client's command-line tool exists on Linux. Its output is matched against the few states this
/// provider knows; anything else, including a platform with no tool, is reported as unknown rather than
/// guessed. The state words below are the ones this provider expects; they have not been checked against a
/// live client, and the tests exercise the mapping with the words as written here.</para>
/// </summary>
public sealed class TwingateNetwork(TwingateOptions options) : INetworkProvider
{
    public string Id => ClusterConstants.Networks.Twingate;

    public async ValueTask<NetworkStatus> DetectAsync(CancellationToken ct)
    {
        var tool = options.Executable.Length > 0 ? options.Executable : ProbeCommand.Find("/usr/bin/twingate", "/usr/local/bin/twingate");
        if (tool is null || !File.Exists(tool)) return new NetworkStatus(Id, EndpointStatus.Unknown, "no-client-tool");
        var output = await ProbeCommand.ReadAsync(tool, ["status"], TimeSpan.FromSeconds(3.5), ct);
        var (status, detail) = ParseStatus(output);
        return new NetworkStatus(Id, status, detail);
    }

    /// <summary>Maps the client's one-word state to a status. Unrecognised text is unknown, never "down".</summary>
    public static (EndpointStatus Status, string? Detail) ParseStatus(string? output) => output?.Trim().ToLowerInvariant() switch
    {
        "online" => (EndpointStatus.Available, null),
        "offline" => (EndpointStatus.Unavailable, "offline"),
        "not-running" => (EndpointStatus.Unavailable, "not-running"),
        "authenticating" => (EndpointStatus.Unavailable, "needs-login"),
        null => (EndpointStatus.Unknown, "unreadable"),
        _ => (EndpointStatus.Unknown, "unrecognised-state"),
    };

    public ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AdvertisedEndpoint>>([.. options.Advertise
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null)
            .OfType<Uri>()
            .Select(url => new AdvertisedEndpoint(Id, url, EndpointReachability.PrivateNetwork))]);

    public EndpointReachability? Classify(Uri url)
    {
        var host = url.Host.TrimEnd('.');
        foreach (var suffix in options.Suffixes)
        {
            var trimmed = suffix.Trim('.');
            if (trimmed.Length == 0) continue;
            if (host.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + trimmed, StringComparison.OrdinalIgnoreCase))
                return EndpointReachability.PrivateNetwork;
        }

        return null;
    }
}
