using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Llm;
using Shouldly;
using Xunit;

namespace Burbuja.DeepSeek.UnitTests;

/// <summary>
/// 2026-09-26 (wave 2, lane CAPS) — the image block on the DeepSeek wire, pinned as golden JSON.
///
/// <para><b>Why a golden.</b> The lead verified on 2026-09-26 that DeepSeek's Anthropic-compatible endpoint accepts
/// <c>{"type":"image","source":{"type":"base64","media_type":"image/png","data":…}}</c> and describes the image
/// (258 input tokens for a 64×32 PNG). ÓPTICA's capability rows go AVAILABLE on that fact, so the exact bytes the
/// provider sends for an image are what must not drift: one renamed key and every image turn is a 400. No
/// network: the replay handler captures the body.</para>
/// </summary>
public sealed class DeepSeekImageWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_image_block_serializes_to_the_anthropic_shape_exactly()
    {
        ContentBlock block = new ImageBlock(new ImageSource("base64", "image/png", "iVBORw0KGgo="));
        System.Text.Json.JsonSerializer.Serialize(block, typeof(ContentBlock))
            .ShouldBe("""{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}""");

        // And it round-trips: a transcript read back is the same block.
        var back = System.Text.Json.JsonSerializer.Deserialize<ContentBlock>(
            """{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"/9j/"}}""");
        back.ShouldBeOfType<ImageBlock>().Source.ShouldBe(new ImageSource("base64", "image/jpeg", "/9j/"));
    }

    [Fact]
    public async Task A_user_message_with_two_images_reaches_the_wire_as_text_then_image_blocks()
    {
        var handler = new ReplayHandler();
        using var provider = ProviderFactory.Create(handler);

        await provider.InvokeAsync(new LlmRequest(
            "deepseek-flash",
            [new InputMessage("user",
            [
                new TextBlock("compara estas dos fotos"),
                new ImageBlock(new ImageSource("base64", "image/png", "AAAA")),
                new ImageBlock(new ImageSource("base64", "image/webp", "BBBB")),
            ])],
            null, null, null, 256, null), Ct);

        var content = JsonNode.Parse(handler.Bodies.ShouldHaveSingleItem())!["messages"]![0]!["content"]!.ToJsonString();
        content.ShouldBe(
            """[{"type":"text","text":"compara estas dos fotos"},"""
            + """{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},"""
            + """{"type":"image","source":{"type":"base64","media_type":"image/webp","data":"BBBB"}}]""");
    }
}
