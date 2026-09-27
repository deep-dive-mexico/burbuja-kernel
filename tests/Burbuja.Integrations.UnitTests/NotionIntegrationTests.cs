using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Secrets;
using Burbuja.Plugins.Policy.Core;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Integrations.UnitTests;

/// <summary>
/// I3 — the Notion workspace, as the third instance of the integration abstraction, and the one the reference
/// kept as a report sink inside its own process.
///
/// <para><b>The facts are about boundaries, not features.</b> A caller for a third-party API is a proxy
/// unless it is made otherwise, so each fact here pins one of the ways it is not: the token lives in a header
/// and nowhere else, an id is validated BEFORE it becomes a path, a listing is clamped and projected, a page
/// is built from a fixed list of fields rather than from the caller's JSON, and with no token nothing is sent
/// at all. The three surfaces of I1–I3 follow one rule; this is that rule proven a third time, on the surface
/// where the write is a publication rather than a message.</para>
///
/// <para><b>The API is a scripted handler.</b> The unit lane reaches nothing and spends nothing: what is
/// asserted is the request this caller builds and the shape it hands back, which is its whole contract.</para>
/// </summary>
public sealed class NotionIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "ntn_1234567890abcdefghijklmnopqrstuvwxyz";
    private const string PageId = "1f2e3d4c5b6a79887766554433221100";

    /// <summary>A scripted Notion: records every request, answers with whatever the fact queued.</summary>
    private sealed class ScriptedNotion : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<(string Method, string Url, string? Authorization, string? Version, string? Body)> Requests { get; } = [];

        public ScriptedNotion Respond(HttpStatusCode status, string body = "{}")
        {
            _responses.Enqueue((status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(",", auth) : null,
                request.Headers.TryGetValues("Notion-Version", out var version) ? string.Join(",", version) : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
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

    private static NotionCaller Caller(ScriptedNotion notion, ISecretResolver? secrets, int maxItems = 25, int maxBlocks = 90) =>
        new(
            Options.Create(new NotionIntegrationOptions { MaxItems = maxItems, MaxBlocksPerCall = maxBlocks }),
            secrets,
            notion);

    private static IntegrationProperties Workspace(string? secretRef = null) => new(
        Kind: IntegrationConstants.KindNotion,
        Transport: new IntegrationTransport("http", Url: "https://api.notion.com/v1", SecretRef: secretRef),
        Tools: [.. NotionCaller.Supported],
        Enabled: true);

    // ---- the protocol, request by request ---------------------------------------------------------

    /// <summary>
    /// The token travels as a bearer header with the pinned version header, and it is nowhere in the answer —
    /// the mailbox's rule, on the surface where the token is a workspace-wide integration secret.
    /// </summary>
    [Fact]
    public async Task The_token_and_the_version_travel_as_headers_and_are_never_echoed()
    {
        var notion = new ScriptedNotion().Respond(HttpStatusCode.OK, """{"name":"burbuja","type":"bot"}""");
        var caller = Caller(notion, new Secret(Token));

        var result = await caller.CallAsync(Workspace(), "notion_status", new JsonObject(), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        var request = notion.Requests.ShouldHaveSingleItem();
        request.Authorization.ShouldBe($"Bearer {Token}");
        request.Version.ShouldBe("2022-06-28");
        request.Url.ShouldBe("https://api.notion.com/v1/users/me");
        result.Output!.ToJsonString().ShouldNotContain(Token);
        (result.Error ?? "").ShouldNotContain(Token);
    }

    /// <summary>
    /// A search is clamped and projected: ids, titles, types and urls, never the page's payload.
    /// </summary>
    [Fact]
    public async Task A_search_is_clamped_and_projected()
    {
        var notion = new ScriptedNotion().Respond(
            HttpStatusCode.OK,
            """
            {"has_more":true,"results":[{"object":"page","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "url":"https://notion.so/x","last_edited_time":"2026-09-13T00:00:00.000Z",
              "properties":{"Name":{"type":"title","title":[{"plain_text":"Weekly report"}]}},
              "child_page":{"title":"leaked"}}]}
            """);
        var caller = Caller(notion, new Secret(Token), maxItems: 25);

        var result = await caller.CallAsync(
            Workspace(),
            "notion_search",
            new JsonObject { ["query"] = "report", ["type"] = "page" },
            Ct);

        result.Ok.ShouldBeTrue(result.Error);
        var request = notion.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Url.ShouldBe("https://api.notion.com/v1/search");
        request.Body!.ShouldContain("\"page_size\":25");
        request.Body!.ShouldContain("\"filter\"");

        var output = result.Output!;
        output["count"]!.GetValue<int>().ShouldBe(1);
        output["truncated"]!.GetValue<bool>().ShouldBeTrue("the server said there was more");
        output["cappedAt"]!.GetValue<int>().ShouldBe(25);
        output["results"]![0]!["title"]!.GetValue<string>().ShouldBe("Weekly report");
        output.ToJsonString().ShouldNotContain("leaked");
    }

    /// <summary>
    /// An id is validated BEFORE it becomes a path. Notion ids are hex and dashes; anything else is refused
    /// with nothing sent, so a crafted id cannot walk the bearer token to another resource.
    /// </summary>
    [Fact]
    public async Task An_id_that_is_not_an_id_is_refused_before_a_path_is_built()
    {
        var notion = new ScriptedNotion();
        var caller = Caller(notion, new Secret(Token));

        foreach (var bad in new[] { "../users/me", "1f2e3d4c/children", "not-an-id!", "short" })
        {
            var result = await caller.CallAsync(
                Workspace(),
                "notion_read_page",
                new JsonObject { ["id"] = bad },
                Ct);

            result.Ok.ShouldBeFalse($"'{bad}' must not reach the wire");
            result.Error!.ShouldContain("hex digits and dashes");
        }

        notion.Requests.ShouldBeEmpty("no path may be built out of an id that is not one");
    }

    /// <summary>
    /// A page is built from a FIXED list of fields: the caller's extra keys are ignored, so the API's other
    /// fields are not reachable through this tool.
    /// </summary>
    [Fact]
    public async Task A_report_is_built_from_a_fixed_field_list_and_nothing_else()
    {
        var notion = new ScriptedNotion().Respond(HttpStatusCode.OK, $$"""{"id":"{{PageId}}","url":"https://notion.so/p"}""");
        var caller = Caller(notion, new Secret(Token));

        var result = await caller.CallAsync(
            Workspace(),
            "notion_create_page",
            new JsonObject
            {
                ["parent"] = new JsonObject { ["databaseId"] = PageId },
                ["title"] = "Nightly · job-7",
                ["status"] = "succeeded",
                ["facts"] = new JsonArray("4m 12s", "$0.03"),
                ["summary"] = "The suite is green.",
                ["pending"] = new JsonArray("tick the box"),
                ["sections"] = new JsonArray(new JsonObject { ["name"] = "Changes", ["text"] = "two files" }),
                ["brief"] = "Write the note.",
                ["diffstat"] = "2 files changed",
                ["link"] = "https://github.com/deep-dive-mexico/burbuja-kernel/pull/1",
                // Not part of the report: a caller must not be able to reach the API's other fields. The
                // markers are ASCII on purpose — a non-ASCII one would be escaped by the serialiser and the
                // assertion below could never fail, which a neuter run proved the first time round.
                ["icon"] = new JsonObject { ["emoji"] = "SKULL-MARKER" },
                ["cover"] = new JsonObject { ["external"] = new JsonObject { ["url"] = "https://evil.example/x.png" } },
            },
            Ct);

        result.Ok.ShouldBeTrue(result.Error);
        var body = JsonNode.Parse(notion.Requests.ShouldHaveSingleItem().Body!)!;
        var types = body["children"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ToList();

        types.ShouldBe(["callout", "paragraph", "to_do", "toggle", "quote", "code", "bookmark"]);
        body["parent"]!["database_id"]!.GetValue<string>().ShouldBe(PageId);
        body.ToJsonString().ShouldNotContain("evil.example");
        body.ToJsonString().ShouldNotContain("SKULL-MARKER");
        result.Output!["url"]!.GetValue<string>().ShouldBe("https://notion.so/p");
    }

    /// <summary>
    /// A report longer than one request is batched at the configured ceiling, and the answer says how the
    /// page was filled — the reference's own 90-block rule.
    /// </summary>
    [Fact]
    public async Task A_long_report_is_batched_at_the_configured_ceiling()
    {
        var notion = new ScriptedNotion()
            .Respond(HttpStatusCode.OK, $$"""{"id":"{{PageId}}","url":"https://notion.so/p"}""")
            .Respond(HttpStatusCode.OK, "{}")
            .Respond(HttpStatusCode.OK, "{}");
        var caller = Caller(notion, new Secret(Token), maxBlocks: 2);

        var result = await caller.CallAsync(
            Workspace(),
            "notion_create_page",
            new JsonObject
            {
                ["parent"] = new JsonObject { ["pageId"] = PageId },
                ["title"] = "long",
                ["pending"] = new JsonArray("a", "b", "c", "d", "e"),
            },
            Ct);

        result.Ok.ShouldBeTrue(result.Error);
        notion.Requests.Count.ShouldBe(3, "one create plus two appends");
        JsonNode.Parse(notion.Requests[0].Body!)!["children"]!.AsArray().Count.ShouldBe(2);
        notion.Requests[1].Method.ShouldBe("PATCH");
        notion.Requests[1].Url.ShouldBe($"https://api.notion.com/v1/blocks/{PageId}/children");
        result.Output!["blocks"]!.GetValue<int>().ShouldBe(5);
        result.Output["appendedInBatches"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// No token, no request — and the refusal names the secret it looked for, which is what makes a
    /// misconfigured workspace fixable rather than mysterious.
    /// </summary>
    [Fact]
    public async Task Without_a_token_nothing_is_sent_and_the_secret_is_named()
    {
        var notion = new ScriptedNotion();
        var caller = Caller(notion, new Secret(null));

        var result = await caller.CallAsync(
            Workspace(secretRef: "notion-ops"),
            "notion_create_page",
            new JsonObject { ["parent"] = new JsonObject { ["pageId"] = PageId }, ["title"] = "x" },
            Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("notion-ops");
        notion.Requests.ShouldBeEmpty("a publish without a token is not a publish");
    }

    /// <summary>The secret is resolved by the NAME the integration carries, so a host can hold two
    /// workspaces and the resource says which.</summary>
    [Fact]
    public async Task The_secret_is_resolved_by_the_name_the_integration_carries()
    {
        var notion = new ScriptedNotion().Respond(HttpStatusCode.OK, """{"name":"burbuja"}""");
        var secrets = new Secret(Token);
        var caller = Caller(notion, secrets);

        await caller.CallAsync(Workspace(secretRef: "notion-ops"), "notion_status", new JsonObject(), Ct);

        secrets.Asked.ShouldBe(["notion-ops"]);
    }

    /// <summary>A tool this caller cannot serve is refused before the token is even resolved, and it names
    /// what it does serve — it refuses what it cannot DO, never what it thinks is risky.</summary>
    [Fact]
    public async Task A_tool_the_caller_cannot_serve_is_refused_before_the_token_is_read()
    {
        var notion = new ScriptedNotion();
        var secrets = new Secret(Token);
        var caller = Caller(notion, secrets);

        var result = await caller.CallAsync(Workspace(), "notion_update_page", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("notion_read_page", customMessage: "the refusal lists what IS served");
        secrets.Asked.ShouldBeEmpty("the caller knows what it implements before it reads a credential");
        notion.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// A workspace refusal is a RESULT with the status and the server's words, and never the response's
    /// headers — a 401's headers describe the token's realm.
    /// </summary>
    [Fact]
    public async Task A_refusal_carries_the_status_and_not_the_headers()
    {
        var notion = new ScriptedNotion().Respond(
            HttpStatusCode.Unauthorized,
            """{"object":"error","code":"unauthorized","message":"API token is invalid."}""");
        var caller = Caller(notion, new Secret(Token));

        var result = await caller.CallAsync(Workspace(), "notion_status", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("401");
        result.Error!.ShouldContain("API token is invalid.");
        result.Output.ShouldBeNull();
    }

    /// <summary>
    /// And the rule the whole track rests on: the tools this caller serves are judged by the REAL classifier,
    /// so a read is an allow and the publish is gray — the caller never decides that itself.
    /// </summary>
    [Fact]
    public void The_callers_tools_are_judged_by_the_real_classifier()
    {
        var classifier = new NotionClassifier();
        var context = new PolicyContext(
            StreamId: "integrations.resource/x",
            WorkRoot: "/tmp",
            ArtifactDir: "/tmp",
            Profile: PolicyProfile.RestrictedProd,
            TargetEnv: "dev",
            HostRepoPath: "/tmp",
            VerifyCommands: [],
            DevHosts: [],
            AllowedSecretIds: []);

        // Every tool the caller serves must be one the rules know: a name no classifier handles classifies as
        // an unknown kind and escalates, which would make a declared tool cost a human decision for no reason.
        foreach (var tool in NotionCaller.Supported)
            classifier.Handles(tool).ShouldBeTrue($"'{tool}' is declared by the caller and must be classified");

        classifier.Classify(new ActionPolicySubject("notion_read_page", new JsonObject(), context)).Tier
            .ShouldBe(RiskTier.Allow);
        classifier.Classify(new ActionPolicySubject("notion_create_page", new JsonObject(), context)).Tier
            .ShouldBe(RiskTier.Gray, "publishing into a shared workspace is somebody else's world");
    }
}
