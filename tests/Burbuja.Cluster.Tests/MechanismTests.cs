using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Burbuja.Cluster.Networks;
using Burbuja.Cluster.Os;
using Burbuja.Cluster.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Both_sides_derive_the_same_node_key_without_sending_a_secret()
    {
        var (token, tokenId, controllerJoinKey) = ClusterSigner.NewJoinToken();
        ClusterSigner.TryParseJoinToken("  " + token + "\n", out var parsedId, out var nodeJoinKey).ShouldBeTrue();
        parsedId.ShouldBe(tokenId);
        nodeJoinKey.ShouldBe(controllerJoinKey);

        var nodeKey = ClusterSigner.NodeKey(nodeJoinKey, "cluster", "studio", "client-nonce", "server-nonce");
        nodeKey.ShouldBe(ClusterSigner.NodeKey(controllerJoinKey, "cluster", "studio", "client-nonce", "server-nonce"));

        // Bound to the node and to both nonces: the same token cannot yield another node's key.
        nodeKey.ShouldNotBe(ClusterSigner.NodeKey(nodeJoinKey, "cluster", "tower", "client-nonce", "server-nonce"));
        nodeKey.ShouldNotBe(ClusterSigner.NodeKey(nodeJoinKey, "cluster", "studio", "client-nonce", "other"));

        ClusterSigner.TryParseJoinToken("bcj_short_x", out _, out _).ShouldBeFalse();
        ClusterSigner.TryParseJoinToken(token.Replace("bcj", "abc"), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_signature_covers_the_method_path_time_nonce_and_body()
    {
        var key = ClusterSigner.NodeKey([1, 2, 3], "c", "n", "a", "b");
        var body = "{\"x\":1}"u8.ToArray();
        var signature = ClusterSigner.SignRequest(key, "POST", "/cluster/v1/nodes/n/sync", "1000", "nonce-nonce-nonce", body);

        ClusterSigner.Matches(signature, signature).ShouldBeTrue();
        ClusterSigner.Matches(signature, null).ShouldBeFalse();
        ClusterSigner.SignRequest(key, "POST", "/cluster/v1/nodes/other/sync", "1000", "nonce-nonce-nonce", body).ShouldNotBe(signature);
        ClusterSigner.SignRequest(key, "POST", "/cluster/v1/nodes/n/sync", "1001", "nonce-nonce-nonce", body).ShouldNotBe(signature);
        ClusterSigner.SignRequest(key, "POST", "/cluster/v1/nodes/n/sync", "1000", "nonce-nonce-nonce", "{\"x\":2}"u8).ShouldNotBe(signature);

        // A response is tied to the request it answers and to its own status, and is never a valid request.
        var response = ClusterSigner.SignResponse(key, "nonce-nonce-nonce", 200, body);
        response.ShouldNotBe(ClusterSigner.SignResponse(key, "another-nonce-000", 200, body));
        response.ShouldNotBe(ClusterSigner.SignResponse(key, "nonce-nonce-nonce", 410, body));
        response.ShouldNotBe(signature);
    }

    [Fact]
    public void A_nonce_is_accepted_once_and_bearers_compare_whole_values()
    {
        var window = new NonceWindow(capacity: 2);
        var now = DateTimeOffset.UnixEpoch;
        window.TryAccept("aaaaaaaaaaaaaaaa", now).ShouldBeTrue();
        window.TryAccept("aaaaaaaaaaaaaaaa", now).ShouldBeFalse();
        window.TryAccept("short", now).ShouldBeFalse();

        ClusterSigner.BearerMatches("token-token", "Bearer token-token").ShouldBeTrue();
        ClusterSigner.BearerMatches("token-token", "Bearer token-toke").ShouldBeFalse();
        ClusterSigner.BearerMatches("token-token", null).ShouldBeFalse();
    }
}

public sealed class NetworkTests
{
    /// <summary>Stands in for an overlay plugin: it claims names under one suffix and nothing else.</summary>
    private sealed class OverlayNetwork : INetworkProvider
    {
        public string Id => "overlay";
        public ValueTask<NetworkStatus> DetectAsync(CancellationToken ct) => ValueTask.FromResult(new NetworkStatus(Id, EndpointStatus.Available));
        public ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(ListenerBinding listener, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AdvertisedEndpoint>>([new(Id, new Uri($"{listener.Scheme}://self.overlay.test:{listener.Port}"), EndpointReachability.PrivateNetwork)]);
        public EndpointReachability? Classify(Uri url) => url.Host.EndsWith(".overlay.test", StringComparison.Ordinal) ? EndpointReachability.PrivateNetwork : null;
    }

    [Theory]
    [InlineData("http://127.0.0.1:18520", EndpointReachability.Loopback)]
    [InlineData("http://localhost:18520", EndpointReachability.Loopback)]
    [InlineData("http://192.168.50.10:18520", EndpointReachability.Lan)]
    [InlineData("http://10.1.2.3:18520", EndpointReachability.Lan)]
    [InlineData("http://172.20.0.2:18520", EndpointReachability.Lan)]
    [InlineData("http://172.32.0.2:18520", EndpointReachability.Public)]
    [InlineData("http://100.100.20.30:18520", EndpointReachability.PrivateNetwork)]
    [InlineData("https://hub.example.com", EndpointReachability.Public)]
    public void An_unclaimed_address_is_classified_by_what_it_can_be(string url, EndpointReachability expected) =>
        AddressRanges.Fallback(new Uri(url)).ShouldBe(expected);

    [Fact]
    public void Routes_are_tried_nearest_first_and_a_provider_claims_its_own_addresses()
    {
        var options = new ClusterOptions { Enabled = true, Role = "agent", NodeId = "studio" };
        options.Agent.Controllers.AddRange([
            "https://hub.example.com",
            "https://hub.overlay.test:8443",
            "http://192.168.50.10:18520",
            "not a url",
            "http://127.0.0.1:18520",
        ]);
        var link = new HttpControllerLink(
            options, [new OverlayNetwork(), new LoopbackNetwork(), new LanNetwork(), new StaticNetwork(options)],
            new HttpClient(), TimeProvider.System, NullLogger<HttpControllerLink>.Instance);

        link.Routes().Select(r => $"{r.Network} {r.Url.Host} {r.Reachability}").ShouldBe([
            "loopback 127.0.0.1 Loopback",
            "lan 192.168.50.10 Lan",
            "overlay hub.overlay.test PrivateNetwork",
            "static hub.example.com Public",
        ]);
    }

    [Fact]
    public async Task Built_in_networks_advertise_only_what_they_own()
    {
        var listener = new ListenerBinding("http", 18520);
        var ct = TestContext.Current.CancellationToken;
        (await new LoopbackNetwork().AdvertiseAsync(listener, ct)).Single().Url.ShouldBe(new Uri("http://127.0.0.1:18520"));
        foreach (var endpoint in await new LanNetwork().AdvertiseAsync(listener, ct))
        {
            endpoint.Reachability.ShouldBe(EndpointReachability.Lan);
            AddressRanges.Fallback(endpoint.Url).ShouldBe(EndpointReachability.Lan);
        }

        var options = new ClusterOptions { Advertise = ["https://hub.example.com:8443", "ftp://nope", "garbage"] };
        var advertised = await new StaticNetwork(options).AdvertiseAsync(listener, ct);
        advertised.Select(e => e.Url.ToString()).ShouldBe(["https://hub.example.com:8443/"]);
        new StaticNetwork(options).Classify(new Uri("https://hub.example.com:8443")).ShouldBeNull();
    }
}

public sealed class OperatingSystemTests
{
    private static readonly LaunchCommand Command = new("/opt/tool", ["--flag", "value with space"], new Dictionary<string, string> { ["A"] = "1" }, "/work");

    [Fact]
    public void A_profile_names_only_the_aspects_it_depends_on()
    {
        OsAspects.RequiredBy(new ExecutionProfile()).ShouldBeEmpty();
        OsAspects.RequiredBy(new ExecutionProfile
        {
            Priority = ExecutionPriority.Background,
            YieldToInteractive = true,
            Limits = new ExecutionLimits { CpuCores = 2, MemoryMb = 512, CpuSet = "0-3" },
            Network = NetworkAccess.None,
            RunAs = "worker",
        }).ShouldBe(["priority", "pause", "cpu-limit", "memory-limit", "cpu-set", "network-isolation", "run-as"]);
    }

    [Fact]
    public void MacOs_expresses_priority_through_taskpolicy_and_admits_it_cannot_limit()
    {
        var adapter = new MacOsExecutionAdapter();
        adapter.Aspects.ShouldBe(["priority", "pause"], ignoreOrder: true);

        adapter.Wrap(Command, new ExecutionProfile(), []).ShouldBe(Command);
        var background = adapter.Wrap(Command, new ExecutionProfile { Priority = ExecutionPriority.Background }, []);
        background.FileName.ShouldBe("/usr/sbin/taskpolicy");
        background.Arguments.ShouldBe(["-b", "/opt/tool", "--flag", "value with space"]);
        adapter.Wrap(Command, new ExecutionProfile { Priority = ExecutionPriority.Idle }, []).Arguments
            .ShouldBe(["-b", "/usr/bin/nice", "-n", "19", "/opt/tool", "--flag", "value with space"]);
    }

    [Fact]
    public void Linux_enforces_a_limit_only_when_its_controller_is_delegated()
    {
        var gpus = new[] { new NodeDevice("gpu", "1", "Compute", "cuda"), new NodeDevice("gpu", "3", "Other", "cuda") };
        var profile = new ExecutionProfile
        {
            Priority = ExecutionPriority.Background,
            Limits = new ExecutionLimits { CpuCores = 2.5m, MemoryMb = 4096, CpuSet = "0-7" },
        };

        var full = new LinuxExecutionAdapter(system: false, controllers: new HashSet<string> { "cpu", "memory", "cpuset", "pids" });
        full.Aspects.ShouldBe(["priority", "pause", "cpu-limit", "memory-limit", "cpu-set"], ignoreOrder: true);
        var wrapped = full.Wrap(Command, profile, gpus);
        wrapped.FileName.ShouldBe("/usr/bin/systemd-run");
        wrapped.Arguments.ShouldBe([
            "--user", "--scope", "--quiet", "--collect",
            "-p", "MemoryMax=4096M", "-p", "MemorySwapMax=0", "-p", "CPUQuota=250%", "-p", "CPUWeight=20", "-p", "AllowedCPUs=0-7",
            "--", "/usr/bin/nice", "-n", "10", "/opt/tool", "--flag", "value with space",
        ]);
        wrapped.Environment["CUDA_VISIBLE_DEVICES"].ShouldBe("1,3");
        wrapped.Environment["A"].ShouldBe("1");

        // Only memory is delegated: the processor limits are neither advertised nor silently half-applied.
        var memoryOnly = new LinuxExecutionAdapter(system: true, controllers: new HashSet<string> { "memory" });
        memoryOnly.Aspects.ShouldBe(["priority", "pause", "memory-limit"], ignoreOrder: true);
        memoryOnly.Wrap(Command, profile, []).Arguments.Take(8).ShouldBe(["--scope", "--quiet", "--collect", "-p", "MemoryMax=4096M", "-p", "MemorySwapMax=0", "--"]);

        // No scopes at all: priority still works, and work that was given no accelerator sees none.
        var bare = new LinuxExecutionAdapter(system: false, controllers: null);
        bare.Aspects.ShouldBe(["priority", "pause"], ignoreOrder: true);
        var plain = bare.Wrap(Command, profile, []);
        plain.FileName.ShouldBe("/usr/bin/nice");
        plain.Environment["CUDA_VISIBLE_DEVICES"].ShouldBe("");
    }

    [Fact]
    public void A_node_refuses_what_its_operator_did_not_allow()
    {
        var node = new AgentOptions { Kinds = ["process"], MaxPriority = ExecutionPriority.Normal };
        var enforceable = new HashSet<string> { "priority", "pause" };
        var devices = new[] { new NodeDevice("gpu", "0", "Desktop", "cuda", Lendable: false), new NodeDevice("gpu", "1", "Compute", "cuda") };
        var spec = Lab.Program("job", "trainer");

        NodeExecutionPolicy.Refusal(spec, node, enforceable, devices).ShouldBeNull();
        NodeExecutionPolicy.Refusal(spec with { Kind = "container" }, node, enforceable, devices)
            .ShouldBe("kind 'container' is not allowed on this node");
        NodeExecutionPolicy.Refusal(spec with { Execution = new ExecutionProfile { Priority = ExecutionPriority.Interactive } }, node, enforceable, devices)
            .ShouldBe("priority 'interactive' is above this node's maximum 'normal'");
        NodeExecutionPolicy.Refusal(spec with { Execution = new ExecutionProfile { Limits = new ExecutionLimits { MemoryMb = 1 } } }, node, enforceable, devices)
            .ShouldBe("this node cannot enforce 'memory-limit'");
        NodeExecutionPolicy.Refusal(spec with { Execution = new ExecutionProfile { Limits = new ExecutionLimits { MemoryMb = 1 }, Strict = false } }, node, enforceable, devices)
            .ShouldBeNull();
        NodeExecutionPolicy.Refusal(spec with { Execution = new ExecutionProfile { Devices = ["0"] } }, node, enforceable, devices)
            .ShouldBe("device '0' is not lent by this node");
    }

    [Fact]
    public void A_lent_device_goes_to_one_workload_at_a_time()
    {
        var ledger = new DeviceLedger();
        ledger.SetDevices([new NodeDevice("gpu", "0", "Desktop", "cuda", Lendable: false), new NodeDevice("gpu", "1", "Compute", "cuda")]);
        var wantsOne = Lab.Program("a", "trainer") with { Requests = new Dictionary<string, decimal> { ["gpu"] = 1 } };

        ledger.Acquire(Lab.Program("none", "trainer")).ShouldBeEmpty();
        ledger.Acquire(wantsOne)!.Single().Id.ShouldBe("1");
        ledger.Acquire(wantsOne with { Name = "b" }).ShouldBeNull();
        ledger.Acquire(wantsOne with { Name = "c", Execution = new ExecutionProfile { Devices = ["0"] } }).ShouldBeNull();

        ledger.Release("a");
        ledger.Acquire(wantsOne with { Name = "b" })!.Single().Id.ShouldBe("1");
    }

    [Fact]
    public void Tool_output_is_parsed_and_unreadable_values_stay_unknown()
    {
        var devices = NvidiaFactsSource.Parse("0, Synthetic RTX 9000, 24576\n1, Synthetic RTX 9000, [N/A]\nnot a row\n");
        devices.Select(d => (d.Id, d.Name, d.Backend, d.MemoryMb)).ShouldBe([
            ("0", "Synthetic RTX 9000", "cuda", 24576m),
            ("1", "Synthetic RTX 9000", "cuda", null),
        ]);

        SystemFactsSource.ParseMac("{ 2.50 1.90 1.80 }\n40\n", 16L * 1024 * 1024 * 1024).ShouldBe((2.50m, 6553m));
        SystemFactsSource.ParseMac("garbage\n", 16L * 1024 * 1024 * 1024).ShouldBe((null, null));
    }

    [Fact]
    public void An_interactive_hold_always_expires()
    {
        var time = new FakeTimeProvider();
        var options = new ClusterOptions();
        options.Agent.InteractiveHoldMaxSeconds = 10;
        var gate = new InteractiveGate(time, options);
        var changes = new List<bool>();
        gate.Changed += changes.Add;

        gate.Hold("player-1", TimeSpan.FromSeconds(5));
        gate.Hold("player-2", TimeSpan.FromHours(1)); // Capped at the node's maximum.
        gate.Busy.ShouldBeTrue();

        gate.Release("player-1");
        gate.Busy.ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(9));
        gate.Busy.ShouldBeTrue();

        // The second holder never released: it died. The cap ends the hold anyway.
        time.Advance(TimeSpan.FromSeconds(2));
        gate.Busy.ShouldBeFalse();
        gate.Holders.ShouldBeEmpty();
        changes.ShouldBe([true, false]);
    }
}
