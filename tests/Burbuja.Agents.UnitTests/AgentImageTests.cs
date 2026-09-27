using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Artifacts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// M14 R12 (request row X1) — an image on `send`, from the door to the model call.
///
/// <para><b>What the row was.</b> K's side had an honest refusal ("no recibí la imagen") because there was
/// nothing to receive: `send` read `mode`/`text`/`dedupKey` and nothing else, the inbox item carried a string,
/// and the runtime built text-only messages — even though <see cref="ImageBlock"/> and
/// <see cref="ModelCapabilities.SupportsImages"/> had existed for milestones. This file pins the three places
/// that had to change: the DOOR (what it accepts and refuses), the EVENT (that an attachment is named and not
/// carried) and the TRANSCRIPT (what the model is actually given, including when it can see nothing).</para>
/// </summary>
public sealed class AgentImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/agents";
    private const string ArtifactKey = "agents/a1/photo-1";

    private static string Key(string name) => $"{AgentsRoot}/{name}".ToLowerInvariant();
    private static string SendPath(string name) => Key(name) + "/send";

    private static async Task<ServiceProvider> AgentWithPhotoAsync(
        Action<IServiceCollection>? extra = null,
        Dictionary<string, string?>? settings = null)
    {
        var sp = Harness.CreateHost(extra, settings);
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Key("a1"), """{"properties":{"displayName":"a1"}}""")).Ok();
        await using var photo = new MemoryStream(Encoding.UTF8.GetBytes("PNG-BYTES-0123456789"));
        await sp.GetRequiredService<IArtifactStore>().PutAsync(ArtifactKey, photo, "image/png", null, Ct);
        return sp;
    }

    /// <summary>One `send` body. The image is spliced in as JSON text so the literal braces in the request
    /// stay literal — the first draft used an interpolated raw string and the compiler refused it, which is a
    /// compile error well spent.</summary>
    private static string SendBody(string text, string imageJson) =>
        $"{{\"mode\":\"followup\",\"text\":\"{text}\",\"image\":{imageJson}}}";

    private const string PngImage = """{"artifactKey":"agents/a1/photo-1","mediaType":"image/png"}""";

    [Fact]
    public void An_image_is_typed_by_its_own_bytes_when_the_store_says_octet_stream()
    {
        // 2026-09-26 (live review): the artifact store answered application/octet-stream and DeepSeek refused the
        // block with a 400 — ÓPTICA's first live image turn. The bytes decide when no image/* type is declared.
        AgentActivationWorker.SniffImageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).ShouldBe("image/png");
        AgentActivationWorker.SniffImageType([0xFF, 0xD8, 0xFF, 0xE0]).ShouldBe("image/jpeg");
        AgentActivationWorker.SniffImageType("GIF89a"u8).ShouldBe("image/gif");
        AgentActivationWorker.SniffImageType("RIFF\0\0\0\0WEBP"u8).ShouldBe("image/webp");
        AgentActivationWorker.SniffImageType("%PDF-1.7"u8).ShouldBeNull();
    }

    [Fact]
    public async Task An_artifact_attachment_is_accepted_and_the_event_names_it_without_carrying_it()
    {
        using var sp = await AgentWithPhotoAsync();

        var accepted = await Harness.Send(
            sp, "POST", SendPath("a1"),
            SendBody("¿qué ves?", PngImage));
        accepted.StatusCode.ShouldBe(202);

        var agent = (await Harness.Send(sp, "GET", Key("a1"))).AsJson()["properties"]!["inbox"]!.AsArray();
        var item = agent.ShouldHaveSingleItem()!;
        item["text"]!.GetValue<string>().ShouldBe("¿qué ves?");
        item["image"]!["artifactKey"]!.GetValue<string>().ShouldBe(ArtifactKey);
        item["image"]!["mediaType"]!.GetValue<string>().ShouldBe("image/png");

        // The ring says an image arrived, where it came from and what kind — and neither the bytes nor a URL.
        // Read every event the host wrote in a bounded window rather than guessing which stream a pre-session
        // `send` names: the first draft guessed (`SessionStreamId(canonical)`) and got zero events, and a fact
        // built on a guessed id proves nothing about the payload it is here to check. The window closes by
        // itself two seconds in.
        var enqueued = await EventsAsync(sp, AgentInboxEvents.Enqueued);
        var evt = enqueued.ShouldHaveSingleItem();
        // The stream is named after the ACTION's canonical path (`…/agents/a1/send`) while the agent has no
        // session yet — a pre-existing naming quirk this round measured and is recording, not fixing: there is
        // one pre-session verb (`send`), so nothing collides today.
        evt.StreamId.ShouldContain("/agents/a1", Case.Insensitive,
            "the message's ring belongs to this agent, whatever the stream is named");
        var payload = evt.Payload!;
        payload["image"]!["kind"]!.GetValue<string>().ShouldBe("artifact");
        payload["image"]!["artifactKey"]!.GetValue<string>().ShouldBe(ArtifactKey);
        payload.ToJsonString().ShouldNotContain("PNG-BYTES", Case.Sensitive,
            "the attachment is a reference in the ring, never the payload");
    }

    [Fact]
    public async Task The_door_refuses_an_attachment_that_points_at_two_things_or_at_none()
    {
        using var sp = await AgentWithPhotoAsync();

        // Neither: nothing to fetch.
        var none = await Harness.Send(sp, "POST", SendPath("a1"), """{"text":"x","image":{}}""");
        none.StatusCode.ShouldBe(400);
        none.Error!.Code.ShouldBe("InvalidRequestContent");
        none.Error.Details[0].Code.ShouldBe("InvalidImage", "the DETAIL carries the code a caller acts on");
        none.Error.Details[0].Message.ShouldContain("exactly one of artifactKey or url");

        // Both: two answers to "which image", and the door does not pick.
        var both = await Harness.Send(
            sp, "POST", SendPath("a1"),
            SendBody("x", """{"artifactKey":"agents/a1/photo-1","url":"https://example.test/a.png"}"""));
        both.StatusCode.ShouldBe(400);
        both.Error!.Details[0].Message.ShouldContain("exactly one of artifactKey or url");
    }

    [Fact]
    public async Task The_door_refuses_an_attachment_that_is_not_an_image_or_is_not_there()
    {
        using var sp = await AgentWithPhotoAsync();

        var notAnImage = await Harness.Send(
            sp, "POST", SendPath("a1"),
            SendBody("x", """{"artifactKey":"agents/a1/photo-1","mediaType":"application/pdf"}"""));
        notAnImage.StatusCode.ShouldBe(400);
        notAnImage.Error!.Details[0].Message.ShouldContain("must be an image type");

        // The artifact is the one thing the door CAN check here, and an attachment pointing at nothing is the
        // claim this kernel refuses to record — "no such artifact" is a different answer from "attached".
        var missing = await Harness.Send(
            sp, "POST", SendPath("a1"), """{"text":"x","image":{"artifactKey":"agents/a1/nope"}}""");
        missing.StatusCode.ShouldBe(400);
        missing.Error!.Details[0].Code.ShouldBe("InvalidImage");
        missing.Error.Details[0].Message.ShouldContain("agents/a1/nope");
        missing.Error.Details[0].Message.ShouldContain("must point at something that exists");

        // A relative or non-http URL is not a URL a provider could fetch.
        var badUrl = await Harness.Send(sp, "POST", SendPath("a1"), """{"text":"x","image":{"url":"file:///etc/passwd"}}""");
        badUrl.StatusCode.ShouldBe(400);
        badUrl.Error!.Details[0].Message.ShouldContain("absolute http(s)");
    }

    [Fact]
    public async Task A_model_that_declares_no_image_support_is_told_so_inside_the_message_it_cannot_see()
    {
        // The scripted provider declares SupportsImages=false — the state every provider in this tree is in —
        // so this is the DEFAULT host, not a rigged one.
        using var sp = await AgentWithPhotoAsync();
        (await Harness.Send(
            sp, "POST", SendPath("a1"),
            SendBody("¿qué ves?", PngImage))).StatusCode.ShouldBe(202);

        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("a1"), Ct);

        var content = await FirstUserContentAsync(sp, "a1");
        content.OfType<ImageBlock>().ShouldBeEmpty("the model declares no image support; sending a block it rejects would be a 400");
        content.OfType<TextBlock>().Any(t => t.Text.Contains("no image support", StringComparison.Ordinal))
            .ShouldBeTrue("the transcript says an image was attached and NOT delivered — never a silent hole: "
                          + string.Join(" | ", content.OfType<TextBlock>().Select(t => t.Text)));
        content.OfType<TextBlock>().Any(t => t.Text.Contains("¿qué ves?", StringComparison.Ordinal))
            .ShouldBeTrue("and the person's text still runs");
    }

    [Fact]
    public async Task A_model_that_can_see_images_gets_the_bytes_as_a_base64_block()
    {
        var vision = new RecordingVisionProvider();
        // The router picks the provider by CONFIGURATION, so registering one is not enough — measured: the
        // first version of this fact registered the fake and the scripted provider answered, which is why the
        // transcript came back with a refusal sentence instead of a block.
        using var sp = await AgentWithPhotoAsync(
            services => services.AddSingleton<ILlmProvider>(vision),
            new Dictionary<string, string?> { ["Agents:ProviderId"] = vision.Id });
        (await Harness.Send(
            sp, "POST", SendPath("a1"),
            SendBody("¿qué ves?", PngImage))).StatusCode.ShouldBe(202);

        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("a1"), Ct);

        // 2026-09-26 (live review): the session log keeps the image by REFERENCE — base64 in the event log was
        // redacted/spilled and DeepSeek answered "base64 decode error" — and the runner resolves it at request time.
        var content = await FirstUserContentAsync(sp, "a1");
        var logged = content.OfType<ImageBlock>().ShouldHaveSingleItem();
        logged.Source.Kind.ShouldBe(AgentTurnRunner.ArtifactImageKind);
        logged.Source.Data.ShouldBeNull("no image bytes in the session log");
        logged.Source.MediaType.ShouldBe("image/png");

        // The provider is asked with the bytes the store held.
        vision.Seen.ShouldNotBeEmpty();
        var sent = vision.Seen[^1].OfType<ImageBlock>().ShouldHaveSingleItem();
        sent.Source.Kind.ShouldBe("base64");
        sent.Source.MediaType.ShouldBe("image/png");
        Encoding.UTF8.GetString(Convert.FromBase64String(sent.Source.Data!)).ShouldBe("PNG-BYTES-0123456789",
            "the bytes the store held are the bytes the model is given");
    }

    /// <summary>
    /// 2026-09-26 (lane CAPS) — a message may carry up to four images (`image` + `images`), and a model that can
    /// see gets every one of them, in order, as base64 blocks after the text. A fifth is refused at the door with
    /// the bound in the sentence, because an accepted `send` is a durable promise.
    /// </summary>
    [Fact]
    public async Task A_message_with_several_images_delivers_each_one_and_a_fifth_is_refused()
    {
        var vision = new RecordingVisionProvider();
        using var sp = await AgentWithPhotoAsync(
            services => services.AddSingleton<ILlmProvider>(vision),
            new Dictionary<string, string?> { ["Agents:ProviderId"] = vision.Id });
        await using (var second = new MemoryStream(Encoding.UTF8.GetBytes("PNG-BYTES-SECOND")))
            await sp.GetRequiredService<IArtifactStore>().PutAsync("agents/a1/photo-2", second, "image/png", null, Ct);

        var tooMany = await Harness.Send(sp, "POST", SendPath("a1"),
            $"{{\"text\":\"x\",\"image\":{PngImage},\"images\":[{PngImage},{PngImage},{PngImage},{PngImage}]}}");
        tooMany.StatusCode.ShouldBe(400);
        tooMany.Error!.Details[0].Message.ShouldContain("at most 4 images");

        (await Harness.Send(sp, "POST", SendPath("a1"),
            $"{{\"text\":\"compara\",\"image\":{PngImage},\"images\":[{{\"artifactKey\":\"agents/a1/photo-2\",\"mediaType\":\"image/png\"}}]}}"))
            .StatusCode.ShouldBe(202);
        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("a1"), Ct);

        (await FirstUserContentAsync(sp, "a1")).OfType<ImageBlock>().Count().ShouldBe(2);
        var images = vision.Seen[^1].OfType<ImageBlock>().ToList();
        images.Count.ShouldBe(2, "the provider was asked with both");
        Encoding.UTF8.GetString(Convert.FromBase64String(images[0].Source.Data!)).ShouldBe("PNG-BYTES-0123456789");
        Encoding.UTF8.GetString(Convert.FromBase64String(images[1].Source.Data!)).ShouldBe("PNG-BYTES-SECOND");
    }

    /// <summary>The first user message of the agent's current session, read out of the session log.</summary>
    private static async Task<IReadOnlyList<ContentBlock>> FirstUserContentAsync(ServiceProvider sp, string name)
    {
        var agent = (await Harness.Send(sp, "GET", Key(name))).AsJson()["properties"]!;
        var session = agent["currentSessionId"]!.GetValue<string>();
        var sessions = (await Harness.Send(sp, "GET", Key(name) + "/sessions")).AsJson()["value"]!.AsArray();
        var properties = sessions.Single(s => s!["name"]!.GetValue<string>() == session)!["properties"]!;

        // The session resource carries its OWN stream id — the authoritative one. Deriving it from the name
        // would be a second model of the same truth, and `SessionLog.DeriveAsync` TAILS: a wrong id does not
        // return empty, it waits for events that will never come. Hence the bound below as well.
        var streamId = properties["streamId"]!.GetValue<string>();
        var lastSeq = properties["lastSeq"]!.GetValue<long>();

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        var history = await sp.GetRequiredService<SessionLog>().DeriveAsync(streamId, lastSeq, bound.Token);
        return history.First(m => m.Role == "user").Content;
    }

    /// <summary>Every event of one type the host wrote, read through a TAIL with a two-second window: the
    /// in-memory log's tail is what a reader has, and the window is what makes "there is exactly one" an
    /// assertion instead of a wait.</summary>
    private static async Task<IReadOnlyList<Burbuja.Core.Abstractions.Events.LedgerEvent>> EventsAsync(
        ServiceProvider sp,
        string type)
    {
        var log = sp.GetRequiredService<Burbuja.Core.Abstractions.Events.IEventLog>();
        var events = new List<Burbuja.Core.Abstractions.Events.LedgerEvent>();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var evt in log.TailAllAsync(
                               Burbuja.Core.Abstractions.Events.RenderLane.Output | Burbuja.Core.Abstractions.Events.RenderLane.Log,
                               0,
                               bound.Token))
            {
                if (evt.Type == type)
                    events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed; what arrived is the evidence. A tail has no "end" to reach.
        }

        return events;
    }

    /// <summary>A provider that can see, so the image path has an end that is not a refusal.</summary>
    private sealed class RecordingVisionProvider : ILlmProvider
    {
        public string Id => "vision-test";

        public List<IReadOnlyList<ContentBlock>> Seen { get; } = [];

        public ModelCapabilities Capabilities(string modelId) =>
            new(SupportsEffort: false, Thinking: "disabled", StructuredOutputs: true, ForcedToolChoice: true,
                MinCacheablePrefix: 1024, ContextWindow: 200_000, SupportsImages: true);

        public ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
        {
            Seen.Add(request.Messages[^1].Content);
            return ValueTask.FromResult(new LlmResponse(
                LlmStopReason.EndTurn,
                [new TextBlock("veo una foto")],
                new Usage(1, 1, 0, 0),
                "end_turn"));
        }
    }
}
