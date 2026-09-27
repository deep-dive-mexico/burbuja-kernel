using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

public sealed class ArmClientExtensionsTests
{
    [Fact]
    public async Task Http_round_trip_sends_method_body_and_if_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var seen = new List<(HttpMethod Method, Uri Uri, string? IfMatch, string? Body)>();
        var handler = new StubHandler(async (req, token) =>
        {
            seen.Add((req.Method, req.RequestUri!, req.Headers.TryGetValues("If-Match", out var match) ? match.Single() : null,
                req.Content is null ? null : await req.Content.ReadAsStringAsync(token)));
            return Json(HttpStatusCode.OK, """{"name":"rg1"}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080/") };
        var client = new HttpArmClient(http, ArmConstants.ApiVersion, "k");

        var got = await client.GetAsync<JsonObject>("/subscriptions/sub1/resourceGroups/rg1", new Dictionary<string, string> { ["$top"] = "1" }, ct);
        got!["name"]!.GetValue<string>().ShouldBe("rg1");

        var put = await client.PutAsync<JsonObject>("/subscriptions/sub1/resourceGroups/rg1", new JsonObject { ["properties"] = new JsonObject() }, ct);
        put!["name"]!.GetValue<string>().ShouldBe("rg1");

        var patch = await client.PatchAsync<JsonObject>("/subscriptions/sub1/resourceGroups/rg1", new JsonObject { ["tags"] = new JsonObject() }, "W/\"1\"", ct);
        patch.ShouldNotBeNull();

        var posted = await client.PostActionAsync<JsonObject>("/subscriptions/sub1/providers/Burbuja.Authorization/register", null, ct);
        posted.ShouldNotBeNull();

        seen.Count.ShouldBe(4);
        seen[0].Method.Method.ShouldBe("GET");
        seen[0].Uri.Query.ShouldContain("top=1");
        seen[0].IfMatch.ShouldBeNull();
        seen[1].Method.Method.ShouldBe("PUT");
        seen[1].Body!.ShouldContain("\"properties\"");
        seen[2].Method.Method.ShouldBe("PATCH");
        seen[2].IfMatch.ShouldBe("W/\"1\"");
        seen[3].Method.Method.ShouldBe("POST");
        seen[3].Uri.AbsolutePath.ShouldBe("/subscriptions/sub1/providers/Burbuja.Authorization/register");
    }

    [Fact]
    public async Task Precondition_failed_becomes_ArmException_with_code_and_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.PreconditionFailed,
            """{"error":{"code":"PreconditionFailed","message":"The provided ETag does not match the current resource."}}""")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080/") };
        var client = new HttpArmClient(http, ArmConstants.ApiVersion);

        var error = await Should.ThrowAsync<ArmException>(async () =>
            await client.PatchAsync<JsonObject>("/subscriptions/sub1/resourceGroups/rg1", new JsonObject(), "W/\"9\"", ct));

        error.Error.Code.ShouldBe("PreconditionFailed");
        error.Error.Message.ShouldBe("The provided ETag does not match the current resource.");
        error.Error.HttpStatus.ShouldBe(412);
        error.Message.ShouldBe(error.Error.Message);
    }

    [Fact]
    public async Task Empty_body_yields_default_and_delete_tolerates_204()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(req => req.Method == "DELETE"
            ? new ArmClientResponse(204, new Dictionary<string, string>(), null, null)
            : new ArmClientResponse(200, new Dictionary<string, string>(), null, null));

        (await client.GetAsync<JsonObject>("/providers", ct: ct)).ShouldBeNull();
        await client.DeleteAsync("/providers/x", "W/\"1\"", ct);

        client.Requests.Count.ShouldBe(2);
        client.Requests[1].Method.ShouldBe("DELETE");
        client.Requests[1].Headers!["If-Match"].ShouldBe("W/\"1\"");
    }

    [Fact]
    public async Task ListAsync_follows_two_pages_and_stops()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(req => req.Query?.ContainsKey("$skipToken") == true
            ? Response(200, """{"value":[{"name":"rg2"}],"nextLink":null}""")
            : Response(200, """{"value":[{"name":"rg1"}],"nextLink":"/subscriptions/sub1/resourceGroups?api-version=2026-09-01&$skipToken=tok"}"""));

        var names = new List<string>();
        await foreach (var item in client.ListAsync<JsonObject>("/subscriptions/sub1/resourceGroups", new Dictionary<string, string> { ["$top"] = "1" }, ct))
            names.Add(item["name"]!.GetValue<string>());

        names.ShouldBe(["rg1", "rg2"]);
        client.Requests.Count.ShouldBe(2);
        client.Requests[0].Query!["$top"].ShouldBe("1");
        client.Requests[1].Path.ShouldBe("/subscriptions/sub1/resourceGroups");
        client.Requests[1].Query!["$skipToken"].ShouldBe("tok");
        client.Requests[1].Query!["api-version"].ShouldBe(ArmConstants.ApiVersion);
        client.Requests[1].Query!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListAsync_stops_when_a_nextLink_repeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(_ =>
            Response(200, """{"value":[{"name":"rg1"}],"nextLink":"/subscriptions/sub1/resourceGroups?$skipToken=loop"}"""));

        var pages = 0;
        await foreach (var _ in client.ListAsync<JsonObject>("/subscriptions/sub1/resourceGroups", ct: ct))
            pages++;

        pages.ShouldBe(2);
        client.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PollOperationAsync_succeeds_after_one_running_poll()
    {
        var ct = TestContext.Current.CancellationToken;
        var polls = 0;
        var client = new FakeArmClient(req =>
        {
            polls++;
            return polls == 1
                ? Response(202, """{"properties":{"status":"Running"}}""")
                : Response(200, """{"name":"op1","properties":{"status":"Succeeded"}}""");
        });
        var accepted = Accepted("Azure-AsyncOperation", "/subscriptions/sub1/providers/Burbuja.Authorization/locations/local/operations/op1?api-version=2026-09-01");

        var result = await client.PollOperationAsync<JsonObject>(accepted, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), ct);

        result!["name"]!.GetValue<string>().ShouldBe("op1");
        polls.ShouldBe(2);
        client.Requests.Count.ShouldBe(2);
        client.Requests[0].Path.ShouldBe("/subscriptions/sub1/providers/Burbuja.Authorization/locations/local/operations/op1");
        client.Requests[0].Query!["api-version"].ShouldBe(ArmConstants.ApiVersion);
        client.Requests[0].Query!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PollOperationAsync_throws_when_the_operation_reports_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(_ =>
            Response(200, """{"properties":{"status":"Failed"},"error":{"code":"LockExists","message":"the operation failed"}}"""));

        var error = await Should.ThrowAsync<ArmException>(async () =>
            await client.PollOperationAsync<JsonObject>(Accepted("Azure-AsyncOperation", "/operations/op1"), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), ct));

        error.Error.Code.ShouldBe("LockExists");
        error.Error.Message.ShouldBe("the operation failed");
    }

    [Fact]
    public async Task PollOperationAsync_throws_when_the_poll_returns_an_error_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(_ => new ArmClientResponse(
            500,
            new Dictionary<string, string>(),
            new JsonObject { ["error"] = new JsonObject { ["code"] = "InternalServerError", ["message"] = "boom" } },
            new ArmError("InternalServerError", "boom", null, [], null, 500)));

        var error = await Should.ThrowAsync<ArmException>(async () =>
            await client.PollOperationAsync<JsonObject>(Accepted("Azure-AsyncOperation", "/operations/op1"), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), ct));

        error.Error.Code.ShouldBe("InternalServerError");
        error.Error.HttpStatus.ShouldBe(500);
    }

    [Fact]
    public async Task PollOperationAsync_falls_back_to_Location_and_times_out_while_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(_ => Response(202, """{"status":"Running"}"""));

        var error = await Should.ThrowAsync<ArmException>(async () =>
            await client.PollOperationAsync<JsonObject>(Accepted("Location", "/operations/op2"), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(5), ct));

        error.Error.Code.ShouldBe("OperationTimedOut");
        client.Requests.Count.ShouldBeGreaterThan(1);
        client.Requests[0].Path.ShouldBe("/operations/op2");
    }

    [Fact]
    public async Task Azure_AsyncOperation_with_api_version_sends_it_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var uris = new List<Uri>();
        var handler = new StubHandler((req, _) =>
        {
            uris.Add(req.RequestUri!);
            return Task.FromResult(Json(HttpStatusCode.OK, """{"properties":{"status":"Succeeded"}}"""));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080/") };
        var client = new HttpArmClient(http, ArmConstants.ApiVersion, "k");
        var accepted = Accepted(
            "Azure-AsyncOperation",
            "/subscriptions/sub1/providers/Burbuja.Authorization/locations/local/operations/op1?api-version=2026-09-01");

        var result = await client.PollOperationAsync<JsonObject>(accepted, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), ct);

        result!["properties"]!["status"]!.GetValue<string>().ShouldBe("Succeeded");
        uris.Count.ShouldBe(1);
        uris[0].AbsolutePath.ShouldBe("/subscriptions/sub1/providers/Burbuja.Authorization/locations/local/operations/op1");
        uris[0].Query.Split("api-version").Length.ShouldBe(2);
        uris[0].Query.ShouldContain("api-version=2026-09-01");
    }

    [Fact]
    public async Task ReadEventsAsync_follows_nextFrom_to_the_end_without_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(req =>
        {
            var from = long.Parse(req.Query!["from"]);
            return from == 0
                ? Response(200, """{"value":[{"seq":1},{"seq":2}],"count":2,"nextFrom":2}""")
                : Response(200, """{"value":[{"seq":3}],"count":1,"nextFrom":null}""");
        });

        var seqs = new List<long>();
        await foreach (var item in client.ReadEventsAsync<JsonObject>("/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Resources/approvals/a1/events", 0, 50, limit: 2, ct: ct))
            seqs.Add(item["seq"]!.GetValue<long>());

        seqs.ShouldBe([1, 2, 3]);
        seqs.Distinct().Count().ShouldBe(seqs.Count);
        client.Requests.Count.ShouldBe(2);
        client.Requests[0].Query!["from"].ShouldBe("0");
        client.Requests[0].Query!["to"].ShouldBe("50");
        client.Requests[0].Query!["limit"].ShouldBe("2");
        client.Requests[1].Query!["from"].ShouldBe("2");
        client.Requests[1].Query!["limit"].ShouldBe("2");
        client.Requests[0].Headers!["Accept"].ShouldBe("application/json");
    }

    [Fact]
    public async Task ReadEventsAsync_stops_on_a_repeated_nextFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(_ => Response(200, """{"value":[{"seq":5}],"count":1,"nextFrom":5}"""));

        var seqs = new List<long>();
        await foreach (var item in client.ReadEventsAsync<JsonObject>("/events", 5, ct: ct))
            seqs.Add(item["seq"]!.GetValue<long>());

        seqs.ShouldBe([5]);
        client.Requests.Count.ShouldBe(1);
    }

    /// <summary>
    /// T4: the search travels as the kernel's `q`, on every page — the filtering is the stream's, and a
    /// client that filtered locally would send the parameter once (or never) and still look correct.
    /// </summary>
    [Fact]
    public async Task ReadEventsAsync_sends_the_search_terms_on_every_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeArmClient(req => long.Parse(req.Query!["from"]) == 0
            ? Response(200, """{"value":[{"seq":1}],"count":1,"nextFrom":9}""")
            : Response(200, """{"value":[{"seq":9}],"count":1,"nextFrom":null}"""));

        var seqs = new List<long>();
        await foreach (var item in client.ReadEventsAsync<JsonObject>("/events", search: "deny the write", ct: ct))
            seqs.Add(item["seq"]!.GetValue<long>());

        seqs.ShouldBe([1, 9]);
        client.Requests.Count.ShouldBe(2);
        client.Requests.ShouldAllBe(r => r.Query!["q"] == "deny the write");

        // No search, no parameter: an empty page read must not grow a `q=` the stream would have to parse.
        var plain = new FakeArmClient(_ => Response(200, """{"value":[],"count":0,"nextFrom":null}"""));
        await foreach (var _ in plain.ReadEventsAsync<JsonObject>("/events", ct: ct))
        {
        }

        plain.Requests[0].Query!.ShouldNotContainKey("q");
    }

    [Fact]
    public async Task TailAsync_yields_the_sse_events_verbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new[]
        {
            new ArmSseEvent("7", "message", """{"seq":7}"""),
            new ArmSseEvent(null, "ping", "{}"),
        };
        var client = new FakeArmClient(_ => Response(200, "{}"), events);

        var seen = new List<ArmSseEvent>();
        await foreach (var ev in client.TailAsync("/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Resources/approvals/a1/events", "6", ct))
            seen.Add(ev);

        seen.ShouldBe(events);
        client.StreamPath.ShouldBe("/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Resources/approvals/a1/events");
        client.LastEventId.ShouldBe("6");
    }

    private static ArmClientResponse Response(int status, string json) =>
        new(status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), JsonNode.Parse(json), null);

    private static ArmClientResponse Accepted(string header, string url) =>
        new(202, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = url }, null, null);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeArmClient(Func<ArmClientRequest, ArmClientResponse> send, ArmSseEvent[]? events = null) : IArmClient
    {
        public List<ArmClientRequest> Requests { get; } = [];
        public string? StreamPath { get; private set; }
        public string? LastEventId { get; private set; }

        public ValueTask<ArmClientResponse> SendAsync(ArmClientRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(send(request));
        }

        public async IAsyncEnumerable<ArmSseEvent> StreamAsync(string path, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
        {
            StreamPath = path;
            LastEventId = lastEventId;
            foreach (var ev in events ?? [])
            {
                await Task.Yield();
                yield return ev;
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            impl(request, cancellationToken);
    }
}
