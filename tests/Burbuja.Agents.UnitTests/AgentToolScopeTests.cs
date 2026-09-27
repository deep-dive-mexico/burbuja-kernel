using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// M5.1 adversarial-review hardening: an agent's tool sandbox is keyed on the agent's identity, and
/// a declared workspace path only counts when it is inside a root the host allows. Both facts are
/// what make "a tool can never reach outside its root" true rather than aspirational.
/// </summary>
public sealed class AgentToolScopeTests
{
    private static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    [Fact]
    public void Two_agents_with_no_display_name_get_two_roots()
    {
        var a = AgentToolScope.ResolveWorkRoot(new AgentProperties(), Key("a"), Empty);
        var b = AgentToolScope.ResolveWorkRoot(new AgentProperties(), Key("b"), Empty);

        a.ShouldNotBe(b, "a shared root is a shared sandbox");
        Directory.Exists(a).ShouldBeTrue();
        Directory.Exists(b).ShouldBeTrue();
    }

    [Fact]
    public void A_display_name_that_normalises_to_nothing_does_not_escape_to_the_parent()
    {
        var root = AgentToolScope.ResolveWorkRoot(new AgentProperties { DisplayName = "!!!" }, Key("odd"), Empty);
        var parent = Path.Combine(Path.GetTempPath(), "burbuja-agents");

        root.ShouldNotBe(parent, "the parent directory holds every other agent's root");
        Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .ShouldBeTrue();
    }

    [Fact]
    public void A_declared_workspace_outside_every_allowed_root_is_ignored()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agent = new AgentProperties { WorkspaceId = home };
        var root = AgentToolScope.ResolveWorkRoot(agent, Key("home"), Empty);

        root.ShouldNotBe(Path.GetFullPath(home));
        AgentToolScope.IsAllowed(home, Empty).ShouldBeFalse("an agent PUT must not hand a tool the home directory");
        AgentToolScope.IsAllowed("/", Empty).ShouldBeFalse();
    }

    [Fact]
    public void A_declared_workspace_inside_an_allowed_root_is_used()
    {
        var allowed = Path.Combine(Path.GetTempPath(), "burbuja-allowed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(allowed);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AgentToolScope.AllowedRootsKey + ":0"] = allowed })
            .Build();

        var agent = new AgentProperties { WorkspaceId = allowed };
        AgentToolScope.ResolveWorkRoot(agent, Key("w"), configuration).ShouldBe(Path.GetFullPath(allowed));
        AgentToolScope.IsAllowed(Path.Combine(allowed, "sub"), configuration).ShouldBeTrue();
    }

    [Fact]
    public void Every_context_gets_a_timeout_and_the_agent_root()
    {
        var context = AgentToolScope.Context(new AgentProperties(), Key("ctx"), "stream", "run", "job", Empty);
        context.WorkRoot.ShouldNotBeNullOrWhiteSpace();
        context.StreamId.ShouldBe("stream");
        context.Timeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    private static string Key(string name) =>
        $"/subscriptions/s1/resourceGroups/rg1/providers/Burbuja.Agents/agents/{name}".ToLowerInvariant();
}
