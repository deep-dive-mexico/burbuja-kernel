using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Clock;
using Burbuja.Mcp;
using Burbuja.Mcp.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Mcp.UnitTests;

/// <summary>
/// K1 — the kernel's own table of MCP sessions, without a transport.
///
/// <para><b>What these facts are about.</b> The SDK owns the transport's sessions; what the kernel adds is the
/// caller on the record, the reaping rules it can state, and the catalogue comparison that gives
/// <c>tools/list_changed</c> something to be true about. Each of those is a rule with an edge — an idle
/// session nobody reaps, a cap that never binds, a fingerprint that ignores a rewritten description — so each
/// has a fact here with a fake clock and, where it matters, a neuter run behind it.</para>
/// </summary>
public sealed class McpSessionRegistryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpCaller Caller(string agent = "worker", string principal = "mcp-lane") => new(
        new ArmPrincipal(principal, "Service", null, [], new Dictionary<string, string>()),
        agent,
        $"/subscriptions/local/resourceGroups/dev/providers/Burbuja.Agents/agents/{agent}",
        agent,
        SessionId: null);

    private static McpSessionRegistry Registry(FakeTimeProvider time, int maxIdle = 1_000, TimeSpan? idle = null) =>
        new(
            new TimeProviderClock(time),
            Options.Create(new McpServerOptions
            {
                MaxIdleSessions = maxIdle,
                SessionIdleTimeout = idle ?? TimeSpan.FromMinutes(30),
            }));

    private static McpToolExport Tool(string name, string description = "does a thing") =>
        new(name, name, description, new JsonObject { ["type"] = "object" }, null);

    /// <summary>The first request opens the record; the second does not, and the count follows.</summary>
    [Fact]
    public void The_first_request_opens_a_record_and_the_next_ones_move_it()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
        var registry = Registry(time);

        var (first, opened) = registry.Seen("s1", Caller());
        opened.ShouldBeTrue();
        first.Requests.ShouldBe(1);
        first.PrincipalId.ShouldBe("mcp-lane");
        first.AgentName.ShouldBe("worker");
        first.CatalogueFingerprint.ShouldBeNull("nothing has been listed on this session yet");

        time.Advance(TimeSpan.FromMinutes(5));
        var (again, openedAgain) = registry.Seen("s1", Caller());

        openedAgain.ShouldBeFalse();
        again.Requests.ShouldBe(2);
        again.OpenedAt.ShouldBe(first.OpenedAt, "opening is once; the record does not restart");
        again.LastSeenAt.ShouldBe(time.GetUtcNow());
        registry.Count.ShouldBe(1);
    }

    /// <summary>Two sessions of one principal are two records: the record is the CONNECTION, not the key.</summary>
    [Fact]
    public void One_principal_can_hold_several_connections()
    {
        var registry = Registry(new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z")));

        registry.Seen("s1", Caller());
        registry.Seen("s2", Caller());

        registry.Count.ShouldBe(2);
        registry.Snapshot().Select(s => s.SessionId).ShouldBe(["s1", "s2"], ignoreOrder: true);
    }

    /// <summary>
    /// A session idle past the timeout is dropped, and one that is merely old is not — the reaping rule is
    /// about the last request, never about when the connection opened.
    /// </summary>
    [Fact]
    public void An_idle_session_is_reaped_and_an_active_one_is_not()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
        var registry = Registry(time, idle: TimeSpan.FromMinutes(30));

        registry.Seen("idle", Caller());
        time.Advance(TimeSpan.FromMinutes(29));
        registry.Seen("busy", Caller());

        time.Advance(TimeSpan.FromMinutes(2)); // "idle" is now 31 minutes old; "busy" is 2.
        registry.Reap().ShouldBe(1);

        registry.Count.ShouldBe(1);
        registry.TryGet("idle", out _).ShouldBeFalse();
        registry.TryGet("busy", out _).ShouldBeTrue();
    }

    /// <summary>
    /// Past the ceiling the OLDEST goes even if it is not idle: a client that never says goodbye must not be
    /// able to grow the table without bound, which is the same rule the SDK applies to its own sessions.
    /// </summary>
    [Fact]
    public void The_ceiling_drops_the_oldest_session_even_when_nothing_is_idle()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
        var registry = Registry(time, maxIdle: 2, idle: TimeSpan.FromHours(10));

        registry.Seen("one", Caller());
        time.Advance(TimeSpan.FromSeconds(1));
        registry.Seen("two", Caller());
        time.Advance(TimeSpan.FromSeconds(1));
        registry.Seen("three", Caller());

        registry.Count.ShouldBe(2);
        registry.TryGet("one", out _).ShouldBeFalse();
        registry.TryGet("three", out _).ShouldBeTrue();
    }

    /// <summary>Closing is what <c>DELETE</c> leads to, and a session that is gone is not found again.</summary>
    [Fact]
    public void Closing_a_session_removes_it()
    {
        var registry = Registry(new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z")));
        registry.Seen("s1", Caller());

        registry.Close("s1").ShouldBeTrue();
        registry.Close("s1").ShouldBeFalse("closing twice is not an error, it is a no-op");
        registry.TryGet("s1", out _).ShouldBeFalse();
    }

    // ---- the catalogue comparison -----------------------------------------------------------------

    /// <summary>
    /// The first listing has nothing to compare against, an identical one is not a change, and a different
    /// one is. The three answers are distinct on purpose: announcing the first listing would tell a client
    /// that a list it just asked for is stale.
    /// </summary>
    [Fact]
    public void The_catalogue_comparison_answers_three_different_things()
    {
        var registry = Registry(new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z")));
        registry.Seen("s1", Caller());

        var before = McpCatalogue.Fingerprint([Tool("read_file"), Tool("bash")]);

        registry.RememberCatalogue("s1", before).ShouldBe(CatalogueChange.First);
        registry.RememberCatalogue("s1", before).ShouldBe(CatalogueChange.Unchanged);
        registry.RememberCatalogue("s1", McpCatalogue.Fingerprint([Tool("read_file")]))
            .ShouldBe(CatalogueChange.Changed, "a tool disappeared");
        registry.RememberCatalogue("s1", McpCatalogue.Fingerprint([Tool("read_file")]))
            .ShouldBe(CatalogueChange.Unchanged, "the new list is what the session has now");
    }

    /// <summary>
    /// A session nobody has seen has nothing to change from, and an unknown id is not a silent "changed":
    /// the notification is only ever sent to a connection the kernel has a record of.
    /// </summary>
    [Fact]
    public void A_catalogue_for_an_unknown_session_is_not_a_change()
    {
        var registry = Registry(new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T10:00:00Z")));

        registry.RememberCatalogue("ghost", "abc").ShouldBe(CatalogueChange.First);
    }

    /// <summary>
    /// The fingerprint follows the NAME and the DESCRIPTION, and ignores the order the tools arrive in — a
    /// catalogue is a set, and re-ordering it is not a change a client needs to hear about.
    /// </summary>
    [Fact]
    public void The_fingerprint_is_about_names_and_descriptions_not_order()
    {
        var one = McpCatalogue.Fingerprint([Tool("read_file"), Tool("bash")]);
        var reordered = McpCatalogue.Fingerprint([Tool("bash"), Tool("read_file")]);
        var rewritten = McpCatalogue.Fingerprint([Tool("read_file", "reads a file"), Tool("bash")]);
        var renamed = McpCatalogue.Fingerprint([Tool("read_file_v2"), Tool("bash")]);

        reordered.ShouldBe(one);
        rewritten.ShouldNotBe(one, "the description is what a model decides from");
        renamed.ShouldNotBe(one);
    }
}
