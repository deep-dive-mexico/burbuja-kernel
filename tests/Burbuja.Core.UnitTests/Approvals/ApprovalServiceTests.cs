using System.Text;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Approvals;

public sealed class ApprovalServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// P1.6: a decision names who made it. The service used to write the literal <c>"human"</c> for every
    /// verb, so a Discord tap, another person's tap and a raw ARM call were indistinguishable in the
    /// record — and the card's "decidido por X" line was rendering a value nothing had ever set.
    /// </summary>
    [Fact]
    public async Task A_decision_records_its_actor()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();

        var id = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        await svc.ApproveAsync(id, "ok", grantScope: null, grantHours: null, CancellationToken.None, actorId: "900000000000000201");

        var record = await svc.GetAsync(id, CancellationToken.None);
        record!.DecidedBy.ShouldBe("900000000000000201");
        record.Outcome.ShouldBeOfType<Approved>();
    }

    [Fact]
    public async Task A_decision_with_no_actor_falls_back_to_human()
    {
        // The fallback exists for a caller that genuinely has no principal (a timer, a test). It is not a
        // default worth reaching for: a record that says "human" about a specific person's tap is worse
        // than one that says nothing.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();

        var id = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        await svc.DenyAsync(id, "no", CancellationToken.None);

        (await svc.GetAsync(id, CancellationToken.None))!.DecidedBy.ShouldBe("human");
    }

    [Fact]
    public async Task Wait_on_already_expired_returns_timed_out()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();
        var id = await svc.OpenAsync(Req(TimeSpan.Zero), CancellationToken.None);
        var outcome = await svc.WaitAsync(id, CancellationToken.None);
        outcome.ShouldBeOfType<TimedOut>();
    }

    [Fact]
    public async Task Expiry_uses_injected_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();
        var id = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var wait = svc.WaitAsync(id, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(6));
        (await wait).ShouldBeOfType<TimedOut>();
    }

    [Fact]
    public async Task Defer_after_returns_deferred_without_closing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time, deferAfter: TimeSpan.FromSeconds(2));
        var svc = sp.GetRequiredService<IApprovalService>();
        var id = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var wait = svc.WaitAsync(id, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3));
        (await wait).ShouldBeOfType<Deferred>();
        var rec = await svc.GetAsync(id, CancellationToken.None);
        rec!.Outcome.ShouldBeNull();
    }

    [Fact]
    public async Task Grant_is_reused_then_dies_with_stream()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();
        var first = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        await svc.ApproveAsync(first, "ok", "scope", 2, CancellationToken.None);
        var second = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var rec = await svc.GetAsync(second, CancellationToken.None);
        rec!.Outcome.ShouldBeOfType<Approved>().Reused.ShouldBeTrue();
        await svc.CancelAsync(second, CancellationToken.None);
        var third = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var thirdRec = await svc.GetAsync(third, CancellationToken.None);
        thirdRec!.Outcome.ShouldBeNull();
    }

    [Fact]
    public async Task Get_list_and_events()
    {
        using var sp = CreateHost(TimeProvider.System);
        var svc = sp.GetRequiredService<IApprovalService>();
        var log = sp.GetRequiredService<IEventLog>();
        var id = await svc.OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        (await svc.GetAsync(id, CancellationToken.None)).ShouldNotBeNull();
        var page = await svc.ListAsync(new ApprovalQuery(null, null, null, "stream-1", true, 10, null), CancellationToken.None);
        page.Value.Count.ShouldBe(1);
        await svc.DenyAsync(id, "no", CancellationToken.None);
        var types = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        try
        {
            await foreach (var evt in log.TailAsync("stream-1", 0, cts.Token))
                types.Add(evt.Type);
        }
        catch (OperationCanceledException)
        {
            // TailAsync is a live subscription.
        }
        types.ShouldContain("core.approval.opened");
        types.ShouldContain("core.approval.denied");
    }

    [Fact]
    public async Task Arm_get_and_approve_action()
    {
        using var sp = CreateHost(TimeProvider.System);
        var seed = await Send(sp, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}""");
        seed.StatusCode.ShouldBeOneOf(200, 201);
        var id = await sp.GetRequiredService<IApprovalService>().OpenAsync(Req(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var get = await Send(sp, "GET", $"/subscriptions/sub1/providers/Burbuja.Resources/approvals/{id}");
        get.StatusCode.ShouldBe(200);
        var approve = await Send(sp, "POST", $"/subscriptions/sub1/providers/Burbuja.Resources/approvals/{id}/approve", """{"note":"go"}""");
        approve.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Action_on_an_unknown_approval_is_404_not_a_fake_decision()
    {
        using var sp = CreateHost(TimeProvider.System);
        (await Send(sp, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}"""))
            .StatusCode.ShouldBeOneOf(200, 201);

        var approve = await Send(
            sp,
            "POST",
            "/subscriptions/sub1/providers/Burbuja.Resources/approvals/no-such-id/approve",
            """{"note":"go"}""");

        approve.StatusCode.ShouldBe(404);
        approve.Error!.Code.ShouldBe("ResourceNotFound");
    }

    /// <summary>A request whose shape matches <paramref name="req"/> but whose decision key differs.</summary>
    private static ApprovalRequest SameShape(ApprovalRequest req, string decisionKey) =>
        req with { DecisionKey = decisionKey };

    [Fact]
    public async Task A_shape_grant_is_consulted_for_a_different_decision_of_the_same_shape()
    {
        // M7.2b: "🔁 similares" pre-approves every later command of the same SHAPE. Those commands
        // have a different decision key by construction — the key comes from the command, the shape
        // from its structure — so a grant table that only ever looked up (decisionKey, shapeKey)
        // together could store a shape grant and never read it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();

        var first = Req(TimeSpan.FromMinutes(5));
        var firstId = await svc.OpenAsync(first, Ct);
        await svc.ApproveAsync(firstId, "ok", GrantScope.Shape(first.ShapeKey!), grantHours: 4, Ct);

        // A different command, same shape: no human is asked again.
        var second = SameShape(first, "deploy:hash:wave1");
        var secondId = await svc.OpenAsync(second, Ct);
        var record = await svc.GetAsync(secondId, Ct);
        var outcome = record!.Outcome.ShouldBeOfType<Approved>();
        outcome.Scope.ShouldBe(GrantScope.Shape(first.ShapeKey!));

        // …and the reused approval is decided, not pending, so a waiter is never left hanging.
        (await svc.WaitAsync(secondId, Ct)).ShouldBeOfType<Approved>();
    }

    [Fact]
    public async Task An_exact_grant_does_not_leak_to_another_shape()
    {
        // The narrow grant must stay narrow: approving one call without a scope pre-approves nothing.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();

        var first = Req(TimeSpan.FromMinutes(5));
        var firstId = await svc.OpenAsync(first, Ct);
        await svc.ApproveAsync(firstId, "ok", grantScope: null, grantHours: 4, Ct);

        var other = SameShape(first, "deploy:hash:wave1") with { ShapeKey = "inmem:other" };
        var otherId = await svc.OpenAsync(other, Ct);
        (await svc.GetAsync(otherId, Ct))!.Outcome.ShouldBeNull("a different shape still needs a human");

        // Even the identical decision key in another stream must not reuse it.
        var elsewhere = first with { StreamId = "stream-2" };
        var elsewhereId = await svc.OpenAsync(elsewhere, Ct);
        (await svc.GetAsync(elsewhereId, Ct))!.Outcome.ShouldBeNull("a grant belongs to its stream");
    }

    [Fact]
    public async Task An_expired_shape_grant_is_not_reused()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var sp = CreateHost(time);
        var svc = sp.GetRequiredService<IApprovalService>();

        var first = Req(TimeSpan.FromMinutes(5));
        var firstId = await svc.OpenAsync(first, Ct);
        await svc.ApproveAsync(firstId, "ok", GrantScope.Shape(first.ShapeKey!), grantHours: 4, Ct);

        time.Advance(TimeSpan.FromHours(5));
        var second = SameShape(first, "deploy:hash:wave1");
        var secondId = await svc.OpenAsync(second, Ct);
        (await svc.GetAsync(secondId, Ct))!.Outcome.ShouldBeNull("the grant expired with the clock");
    }

    [Fact]
    public void The_shape_scope_is_a_wire_string_both_sides_spell_the_same_way()
    {
        GrantScope.Shape("inmem:process").ShouldBe("shape:inmem:process");
        GrantScope.IsShape("shape:inmem:process", "inmem:process").ShouldBeTrue();
        GrantScope.IsShape("shape:other", "inmem:process").ShouldBeFalse();
        GrantScope.IsShape(null, "inmem:process").ShouldBeFalse();
        // A bare class name is a DIFFERENT scope (the risk-class grant) and must not be read as shape.
        GrantScope.IsShape("gray", "gray").ShouldBeFalse();
    }

    private static ApprovalRequest Req(TimeSpan timeout) =>
        new("stream-1", null, ApprovalKind.Confirmation, "deployment",
            "/subscriptions/sub1/resourceGroups/rg/providers/Burbuja.Deployments/environments/e/deployments/d1",
            "deploy:hash:wave0", "inmem:process", "gray", "apply", "preview", "why", [], true, timeout);

    private static ServiceProvider CreateHost(TimeProvider time, TimeSpan? deferAfter = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Policy:DeferAfter"] = deferAfter is { } d ? d.ToString("c") : "00:10:00",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        services.AddSingleton(time);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task<ArmRequestContext> Send(IServiceProvider sp, string method, string path, string? body = null)
    {
        var ctx = new ArmRequestContext
        {
            Method = method,
            Path = path,
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string> { ["x-api-key"] = "owner-key" },
            Body = body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body)),
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        return ctx;
    }
}
