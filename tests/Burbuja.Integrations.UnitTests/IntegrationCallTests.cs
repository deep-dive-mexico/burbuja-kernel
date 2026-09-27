using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Options;
using Burbuja.Core.Policy;
using Burbuja.Core.Redaction;
using Burbuja.Plugins.Policy.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Integrations.UnitTests;

/// <summary>
/// I0 — <c>integrations</c>, and the gate every call goes through.
///
/// <para><b>What these facts are about.</b> A resource that declares an external surface and its tools is a
/// data model until something calls it; the interesting question is what happens at the call. Each fact here
/// pins one of the four answers the kernel can give — declaration is authority, the rules allow it, a human
/// must decide, or it is refused outright — and each asserts the NEGATIVE as well: what the surface received,
/// which is nothing when the answer was no.</para>
///
/// <para><b>The classifiers are the real ones.</b> These facts register <see cref="PolicyCorePlugin"/>'s
/// classifiers rather than doubles, because the point of the milestone is that the browser and mail rules
/// finally have a producer: a fake would make the facts about the fake.</para>
/// </summary>
public sealed class IntegrationCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ResourceId Id(string name = "mailbox") => new()
    {
        SubscriptionId = "local",
        ResourceGroup = "dev",
        Namespace = IntegrationConstants.Namespace,
        TypeSegments = ["integrations"],
        NameSegments = [name],
    };

    /// <summary>A caller that records what it was asked, so "the surface was never reached" is a fact.</summary>
    private sealed class FakeCaller(IReadOnlyList<string>? tools = null, string kind = IntegrationConstants.KindMail)
        : IIntegrationCaller
    {
        public IReadOnlyList<string> Kinds { get; } = [kind];

        public List<string> Listed { get; } = [];

        public List<(string Tool, string Input)> Calls { get; } = [];

        public bool Throw { get; set; }

        public ValueTask<IReadOnlyList<string>> ListToolsAsync(IntegrationProperties integration, CancellationToken ct)
        {
            Listed.Add(integration.Kind);
            if (Throw)
                throw new InvalidOperationException("the surface refused to answer");
            return ValueTask.FromResult(tools ?? ["outlook_read", "outlook_send"]);
        }

        public ValueTask<IntegrationCallResult> CallAsync(
            IntegrationProperties integration,
            string tool,
            JsonNode input,
            CancellationToken ct)
        {
            Calls.Add((tool, input.ToJsonString()));
            return ValueTask.FromResult(new IntegrationCallResult(true, new JsonObject { ["tool"] = tool }));
        }
    }

    private sealed class Fixture
    {
        public required IntegrationHandler Handler { get; init; }

        public required InMemoryResourceStore Store { get; init; }

        public required InMemoryApprovalService Approvals { get; init; }

        public required InMemoryEventLog Events { get; init; }

        public required FakeCaller Caller { get; init; }
    }

    private static async Task<Fixture> BuildAsync(
        IntegrationProperties properties,
        IReadOnlyList<string>? discovered = null,
        bool withCaller = true)
    {
        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryResourceStore(clock);
        var events = new InMemoryEventLog(new CaptureGroupRedactor(), clock);
        var approvals = new InMemoryApprovalService(
            clock, TimeProvider.System, events, Options.Create(new PolicyOptions()), new InMemoryApprovalStore());
        // The REAL policy stack: Policy.Core's own registration, so the rules and classifiers under test
        // are the ones a host runs. Hand-picking two classifiers made the shell path toothless — `rm -rf /`
        // came back gray because no segment rule was registered — which a neuter run exposed when the
        // terminal-tier fact could not be written at all.
        var policyServices = new ServiceCollection();
        new PolicyCorePlugin().ConfigureServices(policyServices);
        // `ActionRequestClassifier` judges named transitions, and an EMPTY catalog is a valid state: a host
        // with no contributor clarifies every named request rather than waving unknown names through.
        policyServices.AddSingleton(new TransitionCatalog([]));
        using var policyProvider = policyServices.BuildServiceProvider();
        var pipeline = new PolicyEngine(
            new ShellCommandParser(),
            policyProvider.GetServices<IPolicyRule<SegmentPolicySubject>>(),
            policyProvider.GetServices<IPolicyRule<ShellLineSubject>>(),
            policyProvider.GetServices<IPolicyClassifier>());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:WorkRoot"] = Path.GetTempPath(),
        }).Build();
        var caller = new FakeCaller(discovered);

        var handler = new IntegrationHandler(
            store, clock, pipeline, approvals, events, new CaptureGroupRedactor(), configuration);

        var id = Id();
        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                id.Canonical, id.NameSegments[^1], IntegrationConstants.ResourceType("integrations"), "local",
                new Dictionary<string, string>(),
                System.Text.Json.JsonSerializer.SerializeToNode(
                    properties, Burbuja.Core.Abstractions.Json.JsonWire.Options)!.AsObject(),
                ProvisioningState.Succeeded,
                new SystemData(clock.UtcNow, "owner", "User", clock.UtcNow, "owner", "User"),
                ""),
            null,
            Ct);

        return new Fixture
        {
            Handler = handler,
            Store = store,
            Approvals = approvals,
            Events = events,
            Caller = caller,
        };
    }

    private static IntegrationProperties DeclaredKind(string kind, params string[] tools) => new(
        Kind: kind,
        Tools: tools,
        Enabled: true);

    private static ActionOutcome Act(
        Fixture fixture,
        string action,
        JsonObject? body = null,
        bool withCaller = true,
        params IIntegrationCaller[] extraCallers) =>
        fixture.Handler.InvokeAsync(
            new ResourceActionRequest
            {
                Id = Id(),
                ApiVersion = "2026-09-01",
                Principal = new ArmPrincipal("owner", "User", null, [], new Dictionary<string, string>()),
                RequestId = "req-1",
                Action = action,
                Body = new JsonObject { ["properties"] = body ?? new JsonObject() },
                Services = withCaller
                    ? new CallerServices([fixture.Caller, .. extraCallers])
                    : new EmptyServices(),
            },
            Ct).AsTask().GetAwaiter().GetResult();

    private static IntegrationProperties Declared(params string[] tools) => new(
        Kind: "mail",
        Tools: tools,
        Enabled: true);

    /// <summary>
    /// Declaration is authority. A tool the surface reports but nobody declared is refused — otherwise a
    /// server that grows a tool overnight gains a way to act, and "nobody has read the new list" would be an
    /// authorisation nobody gave.
    /// </summary>
    [Fact]
    public async Task A_tool_that_is_not_declared_is_refused_and_the_surface_is_never_reached()
    {
        // `outlook_recent` is exactly what a probe would discover; it is NOT in `tools`.
        var fixture = await BuildAsync(Declared("outlook_read"), discovered: ["outlook_read", "outlook_recent"]);

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject { ["tool"] = "outlook_recent" });

        outcome.ShouldBeOfType<ActionOutcome.Rejected>();
        ((ActionOutcome.Rejected)outcome).Error.Message.ShouldContain("not a tool this integration declares");
        fixture.Caller.Calls.ShouldBeEmpty("a refusal must not have called anything");
    }

    /// <summary>A declared tool that the rules ALLOW is called, and the answer comes back.</summary>
    [Fact]
    public async Task A_declared_tool_the_rules_allow_is_called()
    {
        var fixture = await BuildAsync(Declared("outlook_read"));

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "outlook_read",
            ["input"] = new JsonObject { ["query"] = "invoice" },
        });

        var ok = outcome.ShouldBeAssignableTo<ActionOutcome.Ok>();
        ok.Body!["ok"]!.GetValue<bool>().ShouldBeTrue();
        ok.Body["class"]!.GetValue<string>().ShouldBe("mail_read");
        ok.Body["tier"]!.GetValue<string>().ShouldBe("Allow");
        fixture.Caller.Calls.ShouldHaveSingleItem().Tool.ShouldBe("outlook_read");
    }

    /// <summary>
    /// The escalation path, and the retry that finishes it: a write is 202 with an approval id, and the SAME
    /// call with that id approved goes through. The unit lane supplies no human, so this fact approves it
    /// through the same service the portal would.
    /// </summary>
    [Fact]
    public async Task A_write_needs_an_approval_and_the_approved_retry_is_what_calls_it()
    {
        var fixture = await BuildAsync(Declared("outlook_send"));
        var input = new JsonObject { ["to"] = "someone@example.com", ["body"] = "hola" };

        var first = Act(fixture, IntegrationHandler.Call, new JsonObject { ["tool"] = "outlook_send", ["input"] = input.DeepClone() });

        var pending = first.ShouldBeAssignableTo<ActionOutcome.Accepted>();
        var approvalId = pending.OperationId;
        fixture.Caller.Calls.ShouldBeEmpty("a gated call must not have happened yet");

        // The decision the portal would make.
        await fixture.Approvals.ApproveAsync(approvalId, "ok", null, null, Ct, actorId: "owner");

        var second = Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = input.DeepClone(),
            ["approvalId"] = approvalId,
        });

        second.ShouldBeAssignableTo<ActionOutcome.Ok>();
        fixture.Caller.Calls.ShouldHaveSingleItem().Tool.ShouldBe("outlook_send");
    }

    /// <summary>
    /// An approval for a DIFFERENT call does not authorise this one. The decision key carries the
    /// arguments, so approving one send never approves another — the same rule as a grant for one
    /// transition, one layer down.
    /// </summary>
    [Fact]
    public async Task An_approval_for_one_call_does_not_authorise_a_different_one()
    {
        var fixture = await BuildAsync(Declared("outlook_send"));

        var first = (ActionOutcome.Accepted)Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = new JsonObject { ["to"] = "a@example.com" },
        });
        await fixture.Approvals.ApproveAsync(first.OperationId, "ok", null, null, Ct, actorId: "owner");

        var second = Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = new JsonObject { ["to"] = "b@example.com" },
            ["approvalId"] = first.OperationId,
        });

        second.ShouldBeAssignableTo<ActionOutcome.Accepted>("a different message is a different decision");
        fixture.Caller.Calls.ShouldBeEmpty();
    }

    /// <summary>A denied approval is not an approval, and neither is a pending one.</summary>
    [Fact]
    public async Task A_denied_approval_does_not_open_the_call()
    {
        var fixture = await BuildAsync(Declared("outlook_send"));
        var body = new JsonObject { ["tool"] = "outlook_send", ["input"] = new JsonObject { ["to"] = "a@example.com" } };

        var first = (ActionOutcome.Accepted)Act(fixture, IntegrationHandler.Call, body);
        await fixture.Approvals.DenyAsync(first.OperationId, "no", Ct, actorId: "owner");

        var retry = Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = new JsonObject { ["to"] = "a@example.com" },
            ["approvalId"] = first.OperationId,
        });

        retry.ShouldBeAssignableTo<ActionOutcome.Accepted>();
        fixture.Caller.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// The browser rule reaches the classifier through the integration's own dev hosts. A navigation to a
    /// declared dev host is a read the rules allow; anywhere else is gray — and the difference is the
    /// declaration, not the URL string.
    /// </summary>
    [Fact]
    public async Task The_browser_dev_host_rule_reads_the_integrations_own_declaration()
    {
        // The tool name is the one the POLICY dispatches on, which is why it carries the surface prefix:
        // a bare `navigate` is handled by no classifier and escalates (see the fact below).
        var fixture = await BuildAsync(new IntegrationProperties(
            Kind: "browser",
            Tools: ["mcp__browser__navigate"],
            DevHosts: ["dev-api.example.com"],
            Enabled: true));

        // The browser's own caller, because a browser resource routes to the browser and NOT to the mailbox:
        // a fact that handed this resource the mail caller would now be refused for a reason that has nothing
        // to do with the dev-host rule it is about.
        var browser = new FakeCaller(
            ["mcp__browser__navigate"],
            kind: IntegrationConstants.KindBrowser);

        var allowed = Act(
            fixture,
            IntegrationHandler.Call,
            new JsonObject
            {
                ["tool"] = "mcp__browser__navigate",
                ["input"] = new JsonObject { ["url"] = "https://dev-api.example.com/health" },
            },
            extraCallers: browser);

        allowed.ShouldBeAssignableTo<ActionOutcome.Ok>();

        var gated = Act(
            fixture,
            IntegrationHandler.Call,
            new JsonObject
            {
                ["tool"] = "mcp__browser__navigate",
                ["input"] = new JsonObject { ["url"] = "https://elsewhere.example.net/" },
            },
            extraCallers: browser);

        gated.ShouldBeAssignableTo<ActionOutcome.Accepted>("off the dev hosts, a navigation needs a human");
        browser.Calls.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A declared tool that no classifier handles ESCALATES rather than being allowed.
    ///
    /// <para>This is the fail-closed half of the vocabulary rule and the reason `tools` is spelled the way
    /// the policy spells it: the pipeline's answer for a kind nobody handles is <c>unknown_kind</c> at
    /// `Escalate`, so a surface whose tools were declared with friendlier names gets a human rather than a
    /// silent yes. An integration that renamed its tools for readability would not lose a permission — it
    /// would lose every permission, loudly, which is the direction to fail in.</para>
    /// </summary>
    [Fact]
    public async Task A_declared_tool_no_classifier_handles_escalates_rather_than_proceeding()
    {
        // A surface whose tools were declared with friendlier names — the shape of an integration written by
        // somebody who had not read the vocabulary. The kind is the mailbox so that the tool reaches the
        // policy at all: an unreachable kind is refused before anything is judged, which is its own fact.
        var fixture = await BuildAsync(new IntegrationProperties(
            Kind: IntegrationConstants.KindMail,
            Tools: ["navigate"],
            DevHosts: ["dev-api.example.com"],
            Enabled: true));

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "navigate",
            ["input"] = new JsonObject { ["url"] = "https://dev-api.example.com/health" },
        });

        var pending = outcome.ShouldBeAssignableTo<ActionOutcome.Accepted>();
        pending.Body!["class"]!.GetValue<string>().ShouldBe("unknown_kind");
        fixture.Caller.Calls.ShouldBeEmpty();
    }

    /// <summary>A disabled integration refuses before anything else — the switch is the operator's.</summary>
    [Fact]
    public async Task A_disabled_integration_refuses_every_call()
    {
        var fixture = await BuildAsync(new IntegrationProperties(
            Kind: "mail",
            Tools: ["outlook_read"],
            Enabled: false));

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject { ["tool"] = "outlook_read" });

        outcome.ShouldBeOfType<ActionOutcome.Rejected>();
        ((ActionOutcome.Rejected)outcome).Error.Message.ShouldContain("disabled");
        fixture.Caller.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// A host with no caller says so instead of silently succeeding: "this host cannot reach the surface" and
    /// "the surface said no" are different facts, and confusing them reports a broken integration as a policy
    /// decision.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_caller_refuses_and_names_the_configuration()
    {
        var fixture = await BuildAsync(Declared("outlook_read"));

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject { ["tool"] = "outlook_read" }, withCaller: false);

        outcome.ShouldBeOfType<ActionOutcome.Rejected>();
        ((ActionOutcome.Rejected)outcome).Error.Message.ShouldContain("no integration caller");
    }

    /// <summary>
    /// The KIND routes the call, and a host that does not serve it says which kinds it does.
    ///
    /// <para>This is the fact the second caller made necessary. With one registration per type, resolving
    /// "the" caller would hand a browser resource to the mailbox: the refusal would still arrive, but it
    /// would arrive as the mailbox's own words about a tool it does not know, which reads like a policy
    /// answer rather than a missing caller. The message has to name the configuration.</para>
    /// </summary>
    [Fact]
    public async Task A_kind_no_caller_serves_is_refused_with_the_kinds_that_are_served()
    {
        // A tool the rules ALLOW, so the caller check is the only thing that can refuse it: with a gray tool
        // the 202 would arrive first and this fact would be measuring the policy instead of the routing.
        var fixture = await BuildAsync(DeclaredKind("browser", "mcp__browser__read_page"));

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject { ["tool"] = "mcp__browser__read_page" });

        var rejection = outcome.ShouldBeAssignableTo<ActionOutcome.Rejected>();
        rejection.Error.Message.ShouldContain("no integration caller for kind 'browser'");
        rejection.Error.Message.ShouldContain("mail", customMessage: "the refusal has to say what IS served");
        fixture.Caller.Calls.ShouldBeEmpty("a routed-away call must not reach the wrong surface");

        // And nothing was ASKED: no approval card for a call this host cannot make, and no intent event.
        // A card a human can approve but nothing can execute is a decision that authorises nothing.
        var approvals = await fixture.Approvals.ListAsync(
            new ApprovalQuery(null, null, null, null, PendingOnly: true, Top: 50, ContinuationToken: null), Ct);
        approvals.Value.ShouldBeEmpty();
        var events = new List<LedgerEvent>();
        await foreach (var evt in fixture.Events.ReadRangeAsync(
            IntegrationConstants.StreamId(Id().Canonical), 0, long.MaxValue, 50, null, Ct))
        {
            events.Add(evt);
        }

        events.ShouldBeEmpty("nothing was asked, so nothing is recorded as asked");
    }

    /// <summary>
    /// A browser integration reaches the browser caller when both are registered, and the mailbox is not
    /// touched — the positive half of the routing rule, without which the refusal above could pass against a
    /// handler that refused everything.
    /// </summary>
    [Fact]
    public async Task A_browser_resource_reaches_the_browser_caller_and_not_the_mailbox()
    {
        var fixture = await BuildAsync(DeclaredKind("browser", "mcp__browser__navigate"));
        var browser = new FakeCaller(
            ["mcp__browser__navigate", "mcp__browser__read_page"],
            kind: IntegrationConstants.KindBrowser);

        var outcome = Act(
            fixture,
            IntegrationHandler.Call,
            new JsonObject { ["tool"] = "mcp__browser__navigate", ["input"] = new JsonObject { ["url"] = "http://127.0.0.1:8080/" } },
            extraCallers: browser);

        outcome.ShouldBeOfType<ActionOutcome.Ok>();
        browser.Calls.ShouldHaveSingleItem().Tool.ShouldBe("mcp__browser__navigate");
        fixture.Caller.Calls.ShouldBeEmpty("the mailbox must not be handed a browser tool");
    }

    /// <summary>
    /// Two callers claiming one kind is refused rather than resolved by registration order: the order of two
    /// lines in <c>Program.cs</c> is not a decision anybody made, and a host whose behaviour depends on it is
    /// a host whose behaviour nobody can predict.
    /// </summary>
    [Fact]
    public async Task Two_callers_claiming_one_kind_are_refused_rather_than_ordered()
    {
        var fixture = await BuildAsync(Declared("outlook_read"));

        var outcome = Act(
            fixture,
            IntegrationHandler.Call,
            new JsonObject { ["tool"] = "outlook_read" },
            extraCallers: new FakeCaller(["outlook_read"]));

        var rejection = outcome.ShouldBeAssignableTo<ActionOutcome.Rejected>();
        rejection.Error.Message.ShouldContain("2 integration callers claim kind 'mail'");
        fixture.Caller.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// A probe records what the surface offers and changes no authority: what may be CALLED stays what the
    /// human declared, and the fact asserts both halves by calling a discovered-but-undeclared tool after it.
    /// </summary>
    [Fact]
    public async Task A_probe_records_what_the_surface_offers_without_granting_it()
    {
        var fixture = await BuildAsync(Declared("outlook_read"), discovered: ["outlook_read", "outlook_folders"]);

        var outcome = Act(fixture, IntegrationHandler.Probe);

        var ok = outcome.ShouldBeAssignableTo<ActionOutcome.Ok>();
        ok.Body!["tools"]!.AsArray().Count.ShouldBe(2);
        ok.Body["declared"]!.AsArray().Count.ShouldBe(1);

        var stored = await fixture.Store.GetAsync(Id().Key, Ct);
        var properties = stored!.Properties.Deserialize<IntegrationProperties>(
            Burbuja.Core.Abstractions.Json.JsonWire.Options)!;
        properties.DiscoveredTools!.ShouldContain("outlook_folders");
        properties.Tools!.ShouldBe(["outlook_read"], "a probe is not a grant");
        properties.State.ShouldBe("ok");
    }

    /// <summary>A probe against a surface that will not answer records the failure on the resource.</summary>
    [Fact]
    public async Task A_probe_that_fails_records_the_reason_rather_than_raising_it()
    {
        var fixture = await BuildAsync(Declared("outlook_read"));
        fixture.Caller.Throw = true;

        var outcome = Act(fixture, IntegrationHandler.Probe);

        outcome.ShouldBeOfType<ActionOutcome.Rejected>();
        var stored = await fixture.Store.GetAsync(Id().Key, Ct);
        stored!.Properties.Deserialize<IntegrationProperties>(Burbuja.Core.Abstractions.Json.JsonWire.Options)!
            .State!.ShouldContain("refused to answer");
    }

    /// <summary>
    /// Intent before decision, both on the integration's own stream, so the audit reads in the order a person
    /// acted. The tier is on the decision event, which is what an investigation looks for.
    /// </summary>
    [Fact]
    public async Task Every_call_is_audited_as_intent_then_decision()
    {
        var fixture = await BuildAsync(Declared("outlook_read"));

        Act(fixture, IntegrationHandler.Call, new JsonObject { ["tool"] = "outlook_read" });

        var rows = new List<LedgerEvent>();
        await foreach (var e in fixture.Events.ReadRangeAsync(
            IntegrationConstants.StreamId(Id().Canonical), 0, long.MaxValue, 0, null, Ct))
        {
            rows.Add(e);
        }

        rows.Select(r => r.Type).ShouldBe([
            IntegrationHandler.IntentEvent,
            IntegrationHandler.DecisionEvent,
            IntegrationHandler.CalledEvent,
        ]);
        rows[0].StreamSeq.ShouldBeLessThan(rows[1].StreamSeq, "the intent must precede the decision");
        rows[1].Payload!["tier"]!.GetValue<string>().ShouldBe("Allow");
    }

    /// <summary>
    /// The PREVIEW a human is shown is redacted, and that is the layer this handler owns.
    ///
    /// <para>The audit's own redaction is a second, independent layer: `InMemoryEventLog` redacts every
    /// event payload on append, so an assertion about the event would pass with this handler's redaction
    /// removed — which a neuter run showed, and which is why this fact is about the approval instead. The
    /// approval's preview is not an event, so nothing else redacts it, and a mailbox call's preview is
    /// exactly where a message body or a bearer token would otherwise be copied in front of a person.</para>
    /// </summary>
    [Fact]
    public async Task The_approval_preview_a_human_reads_is_redacted()
    {
        var fixture = await BuildAsync(Declared("outlook_send"));

        var pending = (ActionOutcome.Accepted)Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = new JsonObject { ["body"] = "token=ghp_abcdefghijklmnopqrstuvwxyz0123456789" },
        });

        var record = await fixture.Approvals.GetAsync(pending.OperationId, Ct);
        record!.Request.Preview!.ShouldNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        record.Request.Preview.ShouldNotBeNull();
    }

    /// <summary>
    /// A tool the rules put in a TERMINAL tier is refused outright — never called, and never turned into an
    /// approval for a human to wave through.
    ///
    /// <para>This is the fact the neuter run asked for: the first version of this file had no terminal case
    /// at all, so removing `HardDeny` and `Forbidden` from the branch left every fact green. The tool here is
    /// a shell surface — an integration that exposes a command runner is the realistic shape, and `Bash` is
    /// the kind the shell classifier dispatches on — which is what makes the terminal tier reachable at
    /// all: the browser and mail rules produce allows and grays and nothing else.</para>
    /// </summary>
    [Fact]
    public async Task A_terminal_tier_is_refused_and_never_becomes_an_approval()
    {
        var fixture = await BuildAsync(new IntegrationProperties(
            Kind: "mcp",
            Tools: ["Bash"],
            Enabled: true));

        var outcome = Act(fixture, IntegrationHandler.Call, new JsonObject
        {
            ["tool"] = "Bash",
            ["input"] = new JsonObject { ["command"] = "rm -rf /" },
        });

        outcome.ShouldBeOfType<ActionOutcome.Rejected>();
        fixture.Caller.Calls.ShouldBeEmpty("a terminal tier must not reach the surface");
        (await fixture.Approvals.ListAsync(new ApprovalQuery(null, null, null, null, false, 50, null), Ct)).Value
            .ShouldBeEmpty("a hard deny is not a question for a human");
    }

    /// <summary>The descriptor advertises exactly the actions the handler implements.</summary>
    [Fact]
    public void The_descriptor_declares_exactly_the_actions_the_handler_implements()
    {
        var provider = new IntegrationsResourceProvider();
        var descriptor = provider.ResourceTypes.Single(t => t.Type == "integrations");

        descriptor.Actions.Select(a => a.Name).ShouldBe(
            [IntegrationHandler.Probe, IntegrationHandler.Call],
            ignoreOrder: true);
        provider.Namespace.ShouldBe(IntegrationConstants.Namespace);
        IntegrationConstants.Kinds.ShouldBe(["mcp", "mail", "browser", "notion"]);
    }

    /// <summary>An unknown action is refused rather than ignored.</summary>
    [Fact]
    public async Task An_unknown_action_is_refused()
    {
        var fixture = await BuildAsync(Declared("outlook_read"));
        Act(fixture, "teleport").ShouldBeOfType<ActionOutcome.Rejected>();
    }
}

/// <summary>A service provider that resolves one caller and nothing else, for the request's seam.</summary>
/// <summary>
/// The request's service provider, carrying the callers a host would have registered.
///
/// <para>Both shapes are answered on purpose: the single seam I0 resolved by type AND the enumeration the
/// handler now routes by kind. A double that answered only one of them would make the routing facts pass
/// against a resolution path the product no longer uses.</para>
/// </summary>
internal sealed class CallerServices(params IIntegrationCaller[] callers) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType switch
    {
        var t when t == typeof(IIntegrationCaller) => callers.FirstOrDefault(),
        var t when t == typeof(IEnumerable<IIntegrationCaller>) => callers,
        _ => null,
    };
}

internal sealed class EmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
