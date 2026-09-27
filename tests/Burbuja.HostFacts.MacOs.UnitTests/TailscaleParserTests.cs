using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class TailscaleParserTests
{
    [Fact]
    public void A_running_tailnet_is_online_even_though_another_node_is_offline()
    {
        var text = Fixtures.Read("tailscale-status-online.txt");

        // The fixture models a `tailscale status`: this node is the first line and carries no
        // "offline", while a *later* line does. Reading only the first line is what keeps a peer's
        // state from deciding this host's.
        text.ShouldContain("offline");
        TailscaleParser.Parse(text).ShouldBe(true);
    }

    [Fact]
    public void Logged_out_is_a_real_false()
    {
        TailscaleParser.Parse(Fixtures.Read("tailscale-status-logged-out.txt")).ShouldBe(false);
    }

    [Fact]
    public void A_local_node_marked_offline_is_false()
    {
        const string stdout =
            "100.64.0.11    demo-laptop  demo@example     macOS  offline, last seen 3h ago\n";

        TailscaleParser.Parse(stdout).ShouldBe(false);
    }

    [Theory]
    [InlineData("tailscale-status-not-installed.txt")]
    [InlineData("tailscale-status-daemon-down.txt")]
    public void Not_installed_and_cannot_be_asked_are_unknown_never_offline(string fixture)
    {
        // "command not found" is the second machine's answer, "failed to connect … is Tailscale
        // running?" is a daemon that is not there. Neither says the tailnet is down, so neither may
        // come back as false — that would page someone about a host that never had Tailscale.
        TailscaleParser.Parse(Fixtures.Read(fixture)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("The Tailscale GUI failed to start. Try launching the Tailscale application first. CLIError: something")]
    public void No_usable_output_is_unknown(string stdout)
    {
        TailscaleParser.Parse(stdout).ShouldBeNull();
    }
}
