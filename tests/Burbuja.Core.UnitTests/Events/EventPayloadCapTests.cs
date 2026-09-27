using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Clock;
using Burbuja.Core.Events;
using Burbuja.Core.InMemory;
using Burbuja.Core.Redaction;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Events;

/// <summary>
/// C4 (N6): payloads over the cap spill to the artifact store and are replaced by a
/// <c>$spill</c> stub; every tailer gets its own deep clone.
/// </summary>
public sealed class EventPayloadCapTests
{
    [Fact]
    public async Task Payload_over_the_cap_is_spilled()
    {
        var root = TempDir();
        try
        {
            var artifacts = new LocalFileArtifactStore(root, new CaptureGroupRedactor());
            var log = NewLog(artifacts, maxPayloadBytes: 64);
            await log.AppendAsync(Evt("s1", BigPayload(500)), TestContext.Current.CancellationToken);

            var got = await First(log, "s1");
            got.Payload.ShouldNotBeNull();
            got.Payload!["$spill"].ShouldNotBeNull();
            got.Payload!["$spill"]!["artifactKey"]!.GetValue<string>().ShouldStartWith("events/");
            got.Payload!["$spill"]!["bytes"]!.GetValue<int>().ShouldBeGreaterThan(64);
            got.Payload!["$spill"]!["sha256"]!.GetValue<string>().Length.ShouldBe(64);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Payload_under_the_cap_stays_inline()
    {
        var root = TempDir();
        try
        {
            var artifacts = new LocalFileArtifactStore(root, new CaptureGroupRedactor());
            var log = NewLog(artifacts, maxPayloadBytes: 8192);
            await log.AppendAsync(Evt("s1", new JsonObject { ["n"] = 1 }), TestContext.Current.CancellationToken);

            var got = await First(log, "s1");
            got.Payload!["n"]!.GetValue<int>().ShouldBe(1);
            got.Payload!["$spill"].ShouldBeNull();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Spilled_payload_is_byte_identical_to_the_original()
    {
        var root = TempDir();
        try
        {
            var artifacts = new LocalFileArtifactStore(root, new CaptureGroupRedactor());
            var log = NewLog(artifacts, maxPayloadBytes: 64);
            var payload = BigPayload(500);
            await log.AppendAsync(Evt("s1", payload), TestContext.Current.CancellationToken);

            var got = await First(log, "s1");
            var spill = got.Payload!["$spill"]!;
            var key = spill["artifactKey"]!.GetValue<string>();
            var blob = await artifacts.GetAsync(key, TestContext.Current.CancellationToken);
            blob.ShouldNotBeNull();
            using var reader = new StreamReader(blob!.Content, Encoding.UTF8);
            var stored = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            stored.ShouldBe(payload.ToJsonString());
            var expectedSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored))).ToLowerInvariant();
            spill["sha256"]!.GetValue<string>().ShouldBe(expectedSha);

            // The spilled artifact is also reachable through the C1 data-plane key shape.
            got.Payload!["$spill"]!["artifactKey"]!.GetValue<string>()
                .ShouldBe(EventPayloadCap.SpillKey("s1", got.Seq));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Each_tailer_gets_its_own_deep_clone()
    {
        var log = NewLog();
        await log.AppendAsync(Evt("s1", new JsonObject { ["n"] = 1 }), TestContext.Current.CancellationToken);

        var a = await First(log, "s1");
        var b = await First(log, "s1");
        a.Payload!["n"] = 99;

        b.Payload!["n"]!.GetValue<int>().ShouldBe(1);
        var c = await First(log, "s1");
        c.Payload!["n"]!.GetValue<int>().ShouldBe(1);
    }

    private static InMemoryEventLog NewLog(IArtifactStore? spill = null, int maxPayloadBytes = EventPayloadCap.DefaultMaxBytes) =>
        new(new CaptureGroupRedactor(), new TimeProviderClock(TimeProvider.System), 256, spill, maxPayloadBytes);

    private static JsonObject BigPayload(int size) =>
        new() { ["blob"] = new string('x', size) };

    private static EventAppend Evt(string stream, JsonNode payload) =>
        new(stream, null, "big", "big", payload, RenderLane.Output, null, null, null);

    private static async Task<LedgerEvent> First(InMemoryEventLog log, string stream)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await foreach (var e in log.TailAsync(stream, 0, cts.Token))
            return e;
        throw new InvalidOperationException("no event");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
