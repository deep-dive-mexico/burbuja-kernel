using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Secrets;
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
/// I1 — the mailbox, as the first instance of the integration abstraction.
///
/// <para><b>What these facts are about.</b> I0 proved that a surface can be declared and that its calls are
/// decided; this proves the other end — that a real caller speaks to a real protocol without becoming a
/// proxy for it. Every fact here is about a boundary rather than a feature: the token exists only in a
/// header, a listing is bounded and projected, an error is reported without the headers it arrived in, and a
/// tool the caller cannot serve is refused before any request is made.</para>
///
/// <para><b>Graph is a scripted handler, not the network.</b> The unit lane spends nothing and reaches
/// nothing, which is the same discipline the DeepSeek tests follow; what is asserted is the REQUEST this
/// caller builds and the SHAPE it returns, both of which are the caller's whole contract.</para>
/// </summary>
public sealed class MailIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "eyJhbGciOiJIUzI1NiJ9.graph-token-value.abcdef";

    /// <summary>A scripted Graph: records every request, answers with whatever the fact queued.</summary>
    private sealed class ScriptedGraph : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, (string Name, string Value)? Header)> _responses = new();

        public List<(string Method, string Url, string? Authorization, string? Body)> Requests { get; } = [];

        public ScriptedGraph Respond(HttpStatusCode status, string body = "{}", (string Name, string Value)? header = null)
        {
            _responses.Enqueue((status, body, header));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var (status, body, header) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, "{}", ((string, string)?)null);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (header is { } h)
                response.Headers.TryAddWithoutValidation(h.Item1, h.Item2);
            return response;
        }
    }

    private sealed class Secret(string? value) : ISecretResolver
    {
        public List<string> Asked { get; } = [];

        public ValueTask<string?> ResolveAsync(string name, CancellationToken ct)
        {
            Asked.Add(name);
            return ValueTask.FromResult(value);
        }
    }

    private static GraphMailCaller Caller(
        ScriptedGraph graph,
        ISecretResolver? secrets,
        int maxItems = 25) =>
        new(Options.Create(new MailIntegrationOptions { MaxItems = maxItems }), secrets, graph);

    private static IntegrationProperties Mailbox(string? secretRef = null) => new(
        Kind: "mail",
        Transport: new IntegrationTransport("http", Url: "https://graph.microsoft.com/v1.0", SecretRef: secretRef),
        Tools: [.. GraphMailCaller.Supported],
        Enabled: true);

    // ---- the protocol, request by request -------------------------------------------------------

    /// <summary>
    /// A listing is CLAMPED, not refused, and it never asks for bodies. The caller asking for 500 gets the
    /// configured ceiling: an unbounded mailbox read is an exfiltration shape, and the `$select` is fixed so
    /// that a listing cannot pull message bodies into one event either.
    /// </summary>
    [Fact]
    public async Task A_listing_is_bounded_and_asks_for_no_bodies()
    {
        // The answer carries fields this caller does NOT project, so "mapped down" is a fact rather than a
        // claim: a listing that forwarded the server's payload would put the whole message in the event.
        var graph = new ScriptedGraph().Respond(
            HttpStatusCode.OK,
            """{"value":[{"id":"m1","subject":"factura","bodyPreview":"the whole email","body":{"content":"secret"}}]}""");
        var caller = Caller(graph, new Secret(Token));

        var result = await caller.CallAsync(
            Mailbox(), "outlook_search", new JsonObject { ["query"] = "factura", ["top"] = 500 }, Ct);

        result.Ok.ShouldBeTrue();
        var url = graph.Requests.ShouldHaveSingleItem().Url;
        url.ShouldContain("$top=25");
        url.ShouldContain("$select=id,subject,from,receivedDateTime,isRead");
        url.ShouldNotContain("body");
        url.ShouldContain("$search=");

        var projected = result.Output!.ToJsonString();
        projected.ShouldContain("factura");
        projected.ShouldNotContain("the whole email");
        projected.ShouldNotContain("secret");
    }

    /// <summary>The token is a bearer header, and it is the only place it appears.</summary>
    [Fact]
    public async Task The_token_travels_as_a_header_and_is_never_echoed()
    {
        var graph = new ScriptedGraph().Respond(HttpStatusCode.OK, $$"""{"value":[{"id":"m1","subject":"hola"}]}""");
        var caller = Caller(graph, new Secret(Token));

        var result = await caller.CallAsync(Mailbox(), "outlook_recent", new JsonObject(), Ct);

        graph.Requests.ShouldHaveSingleItem().Authorization.ShouldBe($"Bearer {Token}");
        result.Output!.ToJsonString().ShouldNotContain(Token);
        (result.Error ?? "").ShouldNotContain(Token);
        result.Output.ToJsonString().ShouldContain("hola");
    }

    /// <summary>
    /// The secret is looked up by the NAME the integration carries, so a host can hold several mailboxes and
    /// the resource says which one. That is what `transport.secretRef` was documented for.
    /// </summary>
    [Fact]
    public async Task The_secret_is_resolved_by_the_name_the_integration_carries()
    {
        var graph = new ScriptedGraph().Respond(HttpStatusCode.OK, """{"value":[]}""");
        var secrets = new Secret(Token);
        var caller = Caller(graph, secrets);

        await caller.CallAsync(Mailbox(secretRef: "graph-mail-work"), "outlook_recent", new JsonObject(), Ct);

        secrets.Asked.ShouldBe(["graph-mail-work"]);
    }

    /// <summary>
    /// No token, no request. An unauthenticated call to Graph is not a safer call, it is a confusing one:
    /// the caller says what it looked for and sends nothing.
    /// </summary>
    [Fact]
    public async Task Without_a_token_nothing_is_sent_and_the_secret_is_named()
    {
        var graph = new ScriptedGraph();
        var caller = Caller(graph, new Secret(null));

        var result = await caller.CallAsync(Mailbox(secretRef: "graph-mail-work"), "outlook_recent", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("graph-mail-work");
        graph.Requests.ShouldBeEmpty("nothing may be sent without a token");
    }

    /// <summary>
    /// A tool this caller cannot serve is refused BEFORE the token is even resolved — the caller knows what
    /// it implements, and that is a different question from whether the policy allows the action. It is not
    /// a second gatekeeper: it refuses only what it cannot do.
    /// </summary>
    [Fact]
    public async Task A_tool_the_caller_cannot_serve_is_refused_before_anything_else()
    {
        var graph = new ScriptedGraph();
        var secrets = new Secret(Token);
        var caller = Caller(graph, secrets);

        var result = await caller.CallAsync(Mailbox(), "outlook_delete_everything", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("cannot serve");
        secrets.Asked.ShouldBeEmpty("the secret is not consulted for a tool nobody serves");
        graph.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// A Graph refusal is a RESULT, not an exception: the resource records it on the event and answers the
    /// caller, where a throw would escape as a 500 and lose the status that says what happened. Response
    /// headers are deliberately not copied — a 401's `WWW-Authenticate` carries the token's realm.
    /// </summary>
    [Fact]
    public async Task A_refusal_from_the_mailbox_is_reported_with_its_status_and_without_headers()
    {
        var graph = new ScriptedGraph().Respond(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"InvalidAuthenticationToken"}}""",
            ("WWW-Authenticate", "Bearer realm=\"graph\", error=\"invalid_token\""));
        var caller = Caller(graph, new Secret(Token));

        var result = await caller.CallAsync(Mailbox(), "outlook_recent", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("401");
        error.ShouldContain("InvalidAuthenticationToken");
        error.ShouldNotContain(Token);
        // The response's HEADERS are not copied: a 401's `WWW-Authenticate` describes the token's realm and
        // audience, and an event is read by more people than the caller.
        error.ShouldNotContain("WWW-Authenticate");
        error.ShouldNotContain("invalid_token");
    }

    /// <summary>A 2xx whose body is not JSON is a failure, not an invented shape.</summary>
    [Fact]
    public async Task A_successful_answer_that_is_not_json_is_a_failure()
    {
        var graph = new ScriptedGraph().Respond(HttpStatusCode.OK, "not json at all");
        var caller = Caller(graph, new Secret(Token));

        var result = await caller.CallAsync(Mailbox(), "outlook_folders", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("not JSON");
    }

    /// <summary>
    /// A message id is ESCAPED into the path. Without it, an id of `../me/messages?...` would walk the token
    /// to another resource under the same credentials — the classic path-injection shape, one layer below
    /// the policy that cannot see it.
    /// </summary>
    [Fact]
    public async Task A_message_id_is_escaped_into_the_path_rather_than_concatenated()
    {
        var graph = new ScriptedGraph().Respond(HttpStatusCode.OK, """{"id":"../me/messages"}""");
        var caller = Caller(graph, new Secret(Token));

        await caller.CallAsync(Mailbox(), "outlook_read", new JsonObject { ["id"] = "../me/messages?$top=999" }, Ct);

        var url = graph.Requests.ShouldHaveSingleItem().Url;
        url.ShouldNotContain("../");
        url.ShouldContain("%2F");
    }

    /// <summary>
    /// The address Graph is asked at comes from configuration, never from the call. A tool argument that
    /// could name the host would let a caller aim a bearer token at a server of its choosing.
    /// </summary>
    [Fact]
    public async Task The_host_cannot_be_chosen_by_the_caller()
    {
        var graph = new ScriptedGraph().Respond(HttpStatusCode.OK, """{"value":[]}""");
        var caller = Caller(graph, new Secret(Token));

        // EVERY tool that takes an input is asked with a host of the caller's choosing in it. The first
        // version of this fact only asked the two tools that take a query — the two whose input cannot reach
        // a URL at all — so it passed against a caller that DID honour the input, which a neuter run proved
        // by adding exactly that bug. A fact named for a boundary has to exercise the tools that could cross
        // it. (B5's lesson, one milestone on: a fact can be weaker than its name.)
        var hostile = new JsonObject
        {
            ["url"] = "https://attacker.example/steal",
            ["baseUrl"] = "https://attacker.example/steal",
            ["id"] = "m1",
            ["query"] = "factura",
            ["to"] = "ana@example.com",
            ["body"] = "x",
        };

        foreach (var tool in new[] { "outlook_recent", "outlook_search", "outlook_read", "outlook_send" })
            await caller.CallAsync(Mailbox(), tool, hostile.DeepClone(), Ct);

        graph.Requests.Count.ShouldBe(4);
        foreach (var request in graph.Requests)
        {
            request.Url.ShouldStartWith(
                "https://graph.microsoft.com/v1.0/",
                Shouldly.Case.Sensitive,
                $"a request must go to the configured host, whatever the input said: {request.Method} {request.Url}");
        }
    }

    /// <summary>
    /// A send builds a message from a FIXED field list rather than forwarding the caller's JSON, and requires
    /// a recipient and a body. Forwarding would make this caller a proxy for whatever Graph accepts,
    /// including the fields nobody here has considered.
    /// </summary>
    [Fact]
    public async Task A_send_builds_a_message_from_a_fixed_field_list()
    {
        var graph = new ScriptedGraph().Respond(HttpStatusCode.Accepted, "");
        var caller = Caller(graph, new Secret(Token));

        var result = await caller.CallAsync(Mailbox(), "outlook_send", new JsonObject
        {
            ["to"] = "ana@example.com, luis@example.com",
            ["subject"] = "informe",
            ["body"] = "adjunto el informe",
            ["bcc"] = new JsonArray("nobody@example.com"),
            ["internetMessageHeaders"] = new JsonArray(new JsonObject { ["name"] = "X-Evil", ["value"] = "1" }),
        }, Ct);

        result.Ok.ShouldBeTrue();
        var sent = graph.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe("POST");
        sent.Url.ShouldEndWith("/me/sendMail");

        var body = JsonNode.Parse(sent.Body!)!;
        var message = body["message"]!;
        message["toRecipients"]!.AsArray().Count.ShouldBe(2, "a comma-separated list is two recipients");
        message["body"]!["content"]!.GetValue<string>().ShouldBe("adjunto el informe");
        message["bcc"].ShouldBeNull("a field this caller does not build must not be forwarded");
        message["internetMessageHeaders"].ShouldBeNull();
    }

    /// <summary>A message with no recipient or no body is refused, and nothing is sent.</summary>
    [Fact]
    public async Task A_send_without_a_recipient_or_a_body_is_refused()
    {
        var graph = new ScriptedGraph();
        var caller = Caller(graph, new Secret(Token));

        var result = await caller.CallAsync(Mailbox(), "outlook_send", new JsonObject { ["to"] = "ana@example.com" }, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("to");
        graph.Requests.ShouldBeEmpty();
    }

    /// <summary>The vocabulary the caller advertises is what the classifier already knows.</summary>
    [Fact]
    public async Task The_tools_it_serves_are_the_ones_the_mail_classifier_knows()
    {
        var caller = Caller(new ScriptedGraph(), new Secret(Token));
        var tools = await caller.ListToolsAsync(Mailbox(), Ct);

        tools.ShouldContain("outlook_read");
        tools.ShouldContain("outlook_send");
        tools.ShouldNotContain("outlook_delete_everything");

        // Every one of them is classified by MailClassifier, which is what makes a call through the
        // resource decidable at all: a tool the classifier did not know would escalate as `unknown_kind`.
        var classifier = new MailClassifier();
        foreach (var tool in tools)
            classifier.Handles(tool).ShouldBeTrue($"{tool} must be a name MailClassifier dispatches on");
    }

    // ---- through the resource, where the abstraction and the caller meet -------------------------

    /// <summary>
    /// The whole thing, once: a declared mailbox, a READ that the rules allow and that reaches Graph, and a
    /// SEND that the rules gate — 202 with an approval, nothing sent, then one POST after a human approves.
    /// The count is the assertion that matters: a gated call that had already gone out would leave a message
    /// somebody cannot unsend.
    /// </summary>
    [Fact]
    public async Task Through_the_resource_a_read_is_allowed_and_a_send_waits_for_a_human()
    {
        var graph = new ScriptedGraph()
            .Respond(HttpStatusCode.OK, """{"value":[{"id":"m1","subject":"factura"}]}""")
            .Respond(HttpStatusCode.Accepted, "");
        var secrets = new Secret(Token);
        var caller = Caller(graph, secrets);

        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryResourceStore(clock);
        var events = new InMemoryEventLog(new CaptureGroupRedactor(), clock);
        var approvals = new InMemoryApprovalService(
            clock, TimeProvider.System, events, Options.Create(new PolicyOptions()), new InMemoryApprovalStore());

        var policyServices = new ServiceCollection();
        new PolicyCorePlugin().ConfigureServices(policyServices);
        policyServices.AddSingleton(new TransitionCatalog([]));
        using var policyProvider = policyServices.BuildServiceProvider();
        var pipeline = new PolicyEngine(
            new ShellCommandParser(),
            policyProvider.GetServices<IPolicyRule<SegmentPolicySubject>>(),
            policyProvider.GetServices<IPolicyRule<ShellLineSubject>>(),
            policyProvider.GetServices<IPolicyClassifier>());

        var handler = new IntegrationHandler(
            store, clock, pipeline, approvals, events, new CaptureGroupRedactor(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kernel:WorkRoot"] = Path.GetTempPath(),
            }).Build());

        var id = new ResourceId
        {
            SubscriptionId = "local",
            ResourceGroup = "dev",
            Namespace = IntegrationConstants.Namespace,
            TypeSegments = ["integrations"],
            NameSegments = ["mailbox"],
        };
        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                id.Canonical, "mailbox", IntegrationConstants.ResourceType("integrations"), "local",
                new Dictionary<string, string>(),
                System.Text.Json.JsonSerializer.SerializeToNode(Mailbox(), Burbuja.Core.Abstractions.Json.JsonWire.Options)!.AsObject(),
                ProvisioningState.Succeeded,
                new SystemData(clock.UtcNow, "owner", "User", clock.UtcNow, "owner", "User"), ""),
            null, Ct);

        async Task<ActionOutcome> ActAsync(JsonObject body) =>
            await handler.InvokeAsync(new ResourceActionRequest
            {
                Id = id,
                ApiVersion = "2026-09-01",
                Principal = new ArmPrincipal("owner", "User", null, [], new Dictionary<string, string>()),
                RequestId = "req",
                Action = IntegrationHandler.Call,
                Body = new JsonObject { ["properties"] = body },
                Services = new CallerServices(caller),
            }, Ct);

        // A read: allowed by the rules, one GET, no human.
        var read = await ActAsync(new JsonObject { ["tool"] = "outlook_search", ["input"] = new JsonObject { ["query"] = "factura" } });
        read.ShouldBeAssignableTo<ActionOutcome.Ok>();
        graph.Requests.Count.ShouldBe(1);

        // A send: gated. The caller is handed an approval id and Graph has NOT been asked to send anything.
        var send = await ActAsync(new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = new JsonObject { ["to"] = "ana@example.com", ["body"] = "informe" },
        });
        var pending = send.ShouldBeAssignableTo<ActionOutcome.Accepted>();
        graph.Requests.Count.ShouldBe(1, "a gated send must not have gone out");

        await approvals.ApproveAsync(pending.OperationId, "ok", null, null, Ct, actorId: "owner");
        var approved = await ActAsync(new JsonObject
        {
            ["tool"] = "outlook_send",
            ["input"] = new JsonObject { ["to"] = "ana@example.com", ["body"] = "informe" },
            ["approvalId"] = pending.OperationId,
        });

        approved.ShouldBeAssignableTo<ActionOutcome.Ok>();
        graph.Requests.Count.ShouldBe(2);
        graph.Requests[^1].Method.ShouldBe("POST");
        graph.Requests[^1].Url.ShouldEndWith("/me/sendMail");
    }
}
