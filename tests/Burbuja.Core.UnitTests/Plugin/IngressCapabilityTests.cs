using Burbuja.Core.Abstractions.Ingress;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Plugin;

/// <summary>
/// M14 R10 (request row X5) — <c>PluginCapability.HttpEndpoints</c> enforced at boot, which is what the
/// manifest field has been promising since it existed and what nothing read until this round.
///
/// <para><b>The promise is two-way, and both halves are facts.</b> A plugin that declares the capability and
/// mounts nothing is a manifest that overstates what it does; a plugin that mounts a door without declaring
/// it is a door an owner reading the manifest never agreed to. The check uses the <em>descriptor range</em>
/// PluginHost records around each plugin's <c>ConfigureServices</c>, so a plugin cannot satisfy it by naming
/// another plugin's door in its endpoint's own <c>PluginId</c>.</para>
/// </summary>
public sealed class IngressCapabilityTests
{
    [Fact]
    public void A_plugin_that_declares_HttpEndpoints_and_registers_no_door_is_refused_at_boot()
    {
        var plugin = new DoorPlugin("Test.Promises", requires: [PluginCapability.HttpEndpoints], mounts: false);

        var ex = Should.Throw<PluginBootException>(() => Boot(plugin));

        ex.Message.ShouldContain("Test.Promises");
        ex.Message.ShouldContain("requires HttpEndpoints and registers no IHttpIngressEndpoint");
    }

    [Fact]
    public void A_plugin_that_registers_a_door_without_declaring_the_capability_is_refused_at_boot()
    {
        var plugin = new DoorPlugin("Test.Sneaks", requires: [], mounts: true);

        var ex = Should.Throw<PluginBootException>(() => Boot(plugin));

        ex.Message.ShouldContain("Test.Sneaks");
        ex.Message.ShouldContain("without the HttpEndpoints capability");
    }

    [Fact]
    public void A_plugin_that_declares_the_capability_and_mounts_a_door_boots()
    {
        var plugin = new DoorPlugin("Test.Honest", requires: [PluginCapability.HttpEndpoints], mounts: true);
        var services = new ServiceCollection();

        var host = Boot(plugin, services);

        host.Loaded.ShouldHaveSingleItem().Manifest.Id.ShouldBe("Test.Honest");
        services.Count(d => d.ServiceType == typeof(IHttpIngressEndpoint))
            .ShouldBe(1, "the door the plugin registered is in the container the Host will map from");
    }

    /// <summary>
    /// The regression this check could have caused, asserted rather than assumed: a plugin that wants no door
    /// is untouched. Every shipped plugin is in this state today — none declares <c>HttpEndpoints</c> — so a
    /// check that demanded a door from all of them would have refused the whole repo's boot.
    /// </summary>
    [Fact]
    public void A_plugin_that_wants_no_door_is_untouched_by_the_check()
    {
        var plugin = new DoorPlugin("Test.Quiet", requires: [], mounts: false);

        Boot(plugin).Loaded.ShouldHaveSingleItem().Manifest.Id.ShouldBe("Test.Quiet");
    }

    private static PluginHost Boot(IBurbujaPlugin plugin, IServiceCollection? services = null)
    {
        var kernel = Microsoft.Extensions.Options.Options.Create(new KernelOptions
        {
            Role = "control-plane",
            PluginsDirectory = Path.Combine(Path.GetTempPath(), "burbuja-ingress-tests", Guid.NewGuid().ToString("N")),
            ContractsVersion = PluginContracts.Version,
        });
        var host = new PluginHost(new ConfigurationBuilder().Build(), kernel, NullLogger<PluginHost>.Instance, null);
        host.Boot(services ?? new ServiceCollection(), [plugin]);
        return host;
    }

    /// <summary>A plugin whose only variable is the one thing the check is about.</summary>
    private sealed class DoorPlugin(string id, IReadOnlyList<PluginCapability> requires, bool mounts) : IBurbujaPlugin
    {
        public PluginManifest Manifest { get; } = new(
            id, id, "1.0.0", PluginContracts.Version, id + ".dll", "X.Y",
            PluginLoadKind.Static, [], [], requires, ["all"], null, false, [], 1);

        public void ConfigureServices(IServiceCollection services)
        {
            if (mounts)
                services.AddSingleton<IHttpIngressEndpoint>(new Door(id));
        }
    }

    private sealed class Door(string pluginId) : IHttpIngressEndpoint
    {
        public string PluginId { get; } = pluginId;
        public string Route => "a-door";
        public IReadOnlyList<string> Methods => ["POST"];
        public long MaxBodyBytes => 4096;

        public ValueTask<HttpIngressReply> HandleAsync(HttpIngressRequest request, CancellationToken ct) =>
            ValueTask.FromResult(HttpIngressReply.Empty(202));
    }
}
