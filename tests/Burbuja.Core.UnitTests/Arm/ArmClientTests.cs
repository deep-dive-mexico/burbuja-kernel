using System.Net;
using System.Text;
using Burbuja.Core.Arm;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

public sealed class ArmClientTests
{
    [Fact]
    public async Task HttpArmClient_sends_api_version_and_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler((req, _) =>
        {
            req.RequestUri!.Query.ShouldContain("api-version=2026-09-01");
            req.Headers.GetValues("x-api-key").Single().ShouldBe("k");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""", Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080/") };
        var client = new HttpArmClient(http, ArmConstants.ApiVersion, "k");
        var res = await client.SendAsync(new ArmClientRequest("GET", "/providers"), ct);
        res.StatusCode.ShouldBe(200);
        res.Error.ShouldBeNull();
        res.Body!["value"].ShouldNotBeNull();
    }

    [Fact]
    public async Task HttpArmClient_parses_error_envelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"code":"AuthenticationFailed","message":"nope","target":"x-api-key"}}""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080/") };
        var client = new HttpArmClient(http, ArmConstants.ApiVersion, "bad");
        var res = await client.SendAsync(new ArmClientRequest("GET", "/subscriptions"), ct);
        res.StatusCode.ShouldBe(401);
        res.Error!.Code.ShouldBe("AuthenticationFailed");
        res.Error.Target.ShouldBe("x-api-key");
    }

    [Fact]
    public async Task HttpArmClient_streams_sse_and_resumes_from_last_event_id()
    {
        var ct = TestContext.Current.CancellationToken;
        string? seenLast = null;
        var handler = new StubHandler((req, _) =>
        {
            if (req.Headers.TryGetValues("Last-Event-ID", out var values))
                seenLast = values.Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("id: 2\nevent: message\ndata: {\"seq\":2}\n\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080/") };
        var client = new HttpArmClient(http, ArmConstants.ApiVersion, "k");
        var frames = new List<ArmSseEvent>();
        await foreach (var frame in client.StreamAsync("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Resources/approvals/a1/events", "1", ct))
            frames.Add(frame);
        seenLast.ShouldBe("1");
        frames.Count.ShouldBe(1);
        frames[0].Id.ShouldBe("2");
        frames[0].Name.ShouldBe("message");
        frames[0].Data.ShouldContain("\"seq\":2");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(impl(request, cancellationToken));
    }
}
