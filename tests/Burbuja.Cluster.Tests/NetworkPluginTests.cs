using System.Text.Json;
using Burbuja.Cluster.Abstractions;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Plugins.Cluster.Tailscale;
using Burbuja.Plugins.Cluster.Twingate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

public sealed class NetworkPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-networks-" + Guid.NewGuid().ToString("N"));

    public NetworkPluginTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    // A synthetic status document in the shape `tailscale status --json` prints. No real tailnet is described.
    private const string Status = """
        {
          "BackendState": "Running",
          "MagicDNSSuffix": "example-tailnet.ts.net",
          "Self": { "DNSName": "studio.example-tailnet.ts.net.", "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1"], "Online": true },
          "Peer": {
            "nodekey:aaaa": { "DNSName": "tower.example-tailnet.ts.net.", "TailscaleIPs": ["100.64.0.7"] },
            "nodekey:bbbb": { "DNSName": "edge.example-tailnet.ts.net.", "TailscaleIPs": ["100.64.0.9", "not-an-address"] }
          }
        }
        """;

    /// <summary>A stand-in for the client tool that prints a fixed document, so the provider runs its real code path.</summary>
    private string Tool(string output, int exitCode = 0)
    {
        var document = Path.Combine(_root, "output-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(document, output);
        var path = Path.Combine(_root, "tool-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, $"#!/bin/sh\necho \"leaked-secret-on-stderr\" >&2\ncat '{document}'\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Tailscale_status_is_read_into_names_and_addresses()
    {
        var status = TailscaleStatus.Parse(Status)!;
        status.Running.ShouldBeTrue();
        status.DnsName.ShouldBe("studio.example-tailnet.ts.net");
        status.Addresses.ShouldBe(["100.101.102.103", "fd7a:115c:a1e0::1"]);
        status.PeerAddresses.ShouldBe(["100.64.0.7", "100.64.0.9"]);
        status.Suffix.ShouldBe("example-tailnet.ts.net");

        TailscaleStatus.Parse("""{"BackendState":"NeedsLogin"}""")!.Running.ShouldBeFalse();
        TailscaleStatus.Parse("failed to connect to local Tailscale service").ShouldBeNull();
        TailscaleStatus.Parse("[]").ShouldBeNull();
    }

    [Fact]
    public async Task Tailscale_advertises_its_own_addresses_and_claims_only_tailnet_members()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in tool is a shell script");
        var ct = TestContext.Current.CancellationToken;
        var network = new TailscaleNetwork(new TailscaleOptions { Executable = Tool(Status) });

        // Before the tailnet has been read, only the unmistakable name suffix is claimed.
        network.Classify(new Uri("http://100.64.0.7:18520")).ShouldBeNull();
        network.Classify(new Uri("https://anything.ts.net")).ShouldBe(EndpointReachability.PrivateNetwork);

        var detected = await network.DetectAsync(ct);
        detected.Status.ShouldBe(EndpointStatus.Available);
        detected.HostName.ShouldBe("studio.example-tailnet.ts.net");

        (await network.AdvertiseAsync(new ListenerBinding("http", 18520), ct)).Select(e => $"{e.Network} {e.Url} {e.Reachability}").ShouldBe([
            "tailscale http://studio.example-tailnet.ts.net:18520/ PrivateNetwork",
            "tailscale http://100.101.102.103:18520/ PrivateNetwork",
        ]);

        // A peer's address is the tailnet's. The same range used by something else is not, and neither is
        // a name in somebody else's tailnet now that this one's suffix is known.
        network.Classify(new Uri("http://100.64.0.7:18520")).ShouldBe(EndpointReachability.PrivateNetwork);
        network.Classify(new Uri("https://tower.example-tailnet.ts.net:8443")).ShouldBe(EndpointReachability.PrivateNetwork);
        network.Classify(new Uri("https://anything.ts.net")).ShouldBeNull();
        network.Classify(new Uri("http://100.96.0.2:18520")).ShouldBeNull();
        network.Classify(new Uri("http://192.168.1.4:18520")).ShouldBeNull();

        // A node published through `tailscale serve` states that address instead.
        var served = new TailscaleNetwork(new TailscaleOptions { Executable = Tool(Status), Advertise = ["https://studio.example-tailnet.ts.net:8443"] });
        (await served.AdvertiseAsync(new ListenerBinding("http", 18520), ct)).Single().Url.ShouldBe(new Uri("https://studio.example-tailnet.ts.net:8443"));
    }

    [Fact]
    public async Task A_tailnet_that_cannot_be_asked_is_unknown_and_one_that_is_off_is_unavailable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in tool is a shell script");
        var ct = TestContext.Current.CancellationToken;
        var listener = new ListenerBinding("http", 18520);

        var stopped = new TailscaleNetwork(new TailscaleOptions { Executable = Tool("""{"BackendState":"Stopped"}""") });
        (await stopped.DetectAsync(ct)).ShouldBe(new NetworkStatus("tailscale", EndpointStatus.Unavailable, "stopped"));
        (await stopped.AdvertiseAsync(listener, ct)).ShouldBeEmpty();

        // The tool fails and prints something private on standard error: the answer is a fixed label.
        var broken = new TailscaleNetwork(new TailscaleOptions { Executable = Tool("partial", exitCode: 1) });
        (await broken.DetectAsync(ct)).ShouldBe(new NetworkStatus("tailscale", EndpointStatus.Unknown, "unreadable"));

        var missing = new TailscaleNetwork(new TailscaleOptions { Executable = Path.Combine(_root, "absent") });
        (await missing.DetectAsync(ct)).Status.ShouldBe(EndpointStatus.Unknown);
        (await missing.AdvertiseAsync(listener, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Twingate_advertises_what_the_operator_states_and_claims_names_not_numbers()
    {
        var ct = TestContext.Current.CancellationToken;
        var network = new TwingateNetwork(new TwingateOptions
        {
            Executable = Path.Combine(_root, "absent"),
            Advertise = ["http://node-a.corp.internal:18520", "nonsense"],
            Suffixes = ["corp.internal", " . "],
        });

        (await network.AdvertiseAsync(new ListenerBinding("http", 18520), ct)).Select(e => $"{e.Network} {e.Url} {e.Reachability} {e.Status}")
            .ShouldBe(["twingate http://node-a.corp.internal:18520/ PrivateNetwork Unknown"]);

        network.Classify(new Uri("http://node-b.corp.internal:18520")).ShouldBe(EndpointReachability.PrivateNetwork);
        network.Classify(new Uri("http://corp.internal")).ShouldBe(EndpointReachability.PrivateNetwork);
        network.Classify(new Uri("http://notcorp.internal")).ShouldBeNull();

        // The synthetic address a client hands out for an alias is in the range other overlays use, so it is not claimed.
        network.Classify(new Uri("http://100.96.0.2:18520")).ShouldBeNull();

        // No client tool here: whether this machine can dial an alias is unknown, not "no".
        (await network.DetectAsync(ct)).ShouldBe(new NetworkStatus("twingate", EndpointStatus.Unknown, "no-client-tool"));
    }

    [Theory]
    [InlineData("online\n", EndpointStatus.Available, null)]
    [InlineData("Offline", EndpointStatus.Unavailable, "offline")]
    [InlineData("not-running", EndpointStatus.Unavailable, "not-running")]
    [InlineData("authenticating", EndpointStatus.Unavailable, "needs-login")]
    [InlineData("something new in a later client", EndpointStatus.Unknown, "unrecognised-state")]
    [InlineData(null, EndpointStatus.Unknown, "unreadable")]
    public void Twingate_client_state_is_mapped_and_anything_unrecognised_stays_unknown(string? output, EndpointStatus status, string? detail) =>
        TwingateNetwork.ParseStatus(output).ShouldBe((status, detail));

    [Theory]
    [InlineData("tailscale.json", typeof(TailscalePlugin))]
    [InlineData("twingate.json", typeof(TwingatePlugin))]
    public void The_shipped_manifest_says_what_the_code_says(string file, Type pluginType)
    {
        var code = ((IBurbujaPlugin)Activator.CreateInstance(pluginType)!).Manifest;
        using var disk = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifests", file)));
        var root = disk.RootElement;

        root.GetProperty("id").GetString().ShouldBe(code.Id);
        root.GetProperty("name").GetString().ShouldBe(code.Name);
        root.GetProperty("version").GetString().ShouldBe(code.Version);
        root.GetProperty("contracts_version").GetString().ShouldBe(code.ContractsVersion);
        root.GetProperty("assembly").GetString().ShouldBe(code.Assembly);
        root.GetProperty("assembly").GetString().ShouldBe(pluginType.Assembly.GetName().Name + ".dll");
        root.GetProperty("entry_type").GetString().ShouldBe(pluginType.FullName);
        root.GetProperty("load").GetString().ShouldBe("dynamic");
        Strings("provides").ShouldBe(code.Provides);
        Strings("extension_points").ShouldBe(code.ExtensionPoints);
        Strings("requires").ShouldBe(code.Requires.Select(r => r.ToString()));
        Strings("roles").ShouldBe(code.Roles);
        root.GetProperty("config_section").GetString().ShouldBe(code.ConfigSection);
        root.GetProperty("trusted").GetBoolean().ShouldBe(code.Trusted);
        Strings("depends_on").ShouldBe(code.DependsOn);
        root.GetProperty("load_order").GetInt32().ShouldBe(code.LoadOrder);

        IEnumerable<string> Strings(string name) => root.GetProperty(name).EnumerateArray().Select(e => e.GetString()!);
    }

    [Fact]
    public void A_plugin_contributes_its_network_as_an_ordinary_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Twingate:Suffixes:0"] = "corp.internal",
        }).Build());
        new TailscalePlugin().ConfigureServices(services);
        new TwingatePlugin().ConfigureServices(services);

        var networks = services.BuildServiceProvider().GetServices<INetworkProvider>().ToArray();
        networks.Select(n => n.Id).ShouldBe(["tailscale", "twingate"]);
        networks[1].Classify(new Uri("http://node.corp.internal")).ShouldBe(EndpointReachability.PrivateNetwork);
    }
}
