using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Redaction;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Redaction;

public sealed class RedactorTests
{
    private readonly CaptureGroupRedactor _redactor = new();

    public static TheoryData<string, string> AllPatterns()
    {
        var data = new TheoryData<string, string>();
        foreach (var (sample, label) in Samples())
            data.Add(sample, label);
        return data;
    }

    [Fact]
    public void Has_the_pinned_number_of_patterns()
    {
        CaptureGroupRedactor.Patterns.Length.ShouldBe(CaptureGroupRedactor.PatternCount);
        CaptureGroupRedactor.PatternCount.ShouldBe(26);
        Samples().Count.ShouldBe(26);
    }

    [Theory]
    [MemberData(nameof(AllPatterns))]
    public void Pattern_redacts_sample(string sample, string label)
    {
        var outText = _redactor.Redact($"prefix {sample} suffix");
        outText.ShouldContain($"[redacted:{label}]");
        outText.ShouldNotContain("[redacted:[redacted");
    }

    [Fact]
    public void Overlapping_aws_key_patterns_redact_once()
    {
        const string key = "AKIAIOSFODNN7EXAMPLE";
        var text = _redactor.Redact($"id={key}");
        text.ShouldContain("[redacted:aws-access-key]");
        text.ShouldNotContain("[redacted:[redacted");
        var count = 0;
        for (var i = 0; i <= text.Length - "[redacted:".Length; i++)
        {
            if (text.AsSpan(i).StartsWith("[redacted:"))
                count++;
        }

        count.ShouldBe(1);
    }

    [Fact]
    public void Anthropic_key_wins_over_generic_sk_prefix()
    {
        var key = "sk-ant-" + new string('a', 24);
        _redactor.Redact(key).ShouldContain("[redacted:anthropic-key]");
        _redactor.Redact(key).ShouldNotContain("[redacted:api-key]");
    }

    [Fact]
    public async Task Event_append_redacts_summary_and_payload()
    {
        var log = new InMemoryEventLog(_redactor, new TimeProviderClock(TimeProvider.System));
        const string key = "AKIAIOSFODNN7EXAMPLE";
        await log.AppendAsync(new EventAppend(
            "s", null, "t", $"leak {key}",
            new JsonObject { ["token"] = key },
            RenderLane.Log, null, null, null), TestContext.Current.CancellationToken);
        LedgerEvent? got = null;
        await foreach (var e in log.TailAsync("s", 0, TestContext.Current.CancellationToken))
        {
            got = e;
            break;
        }

        got.ShouldNotBeNull();
        got!.Summary.ShouldContain("[redacted:aws-access-key]");
        got.Payload!["token"]!.GetValue<string>().ShouldContain("[redacted:aws-access-key]");
    }

    [Fact]
    public async Task Artifact_write_redacts_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-redact-" + Guid.NewGuid().ToString("N"));
        var store = new LocalFileArtifactStore(dir, _redactor);
        const string key = "AKIAIOSFODNN7EXAMPLE";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes($"secret {key}"));
        await store.PutAsync("notes.txt", input, "text/plain", null, TestContext.Current.CancellationToken);
        var blob = await store.GetAsync("notes.txt", TestContext.Current.CancellationToken);
        blob.ShouldNotBeNull();
        using var reader = new StreamReader(blob!.Content);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("[redacted:aws-access-key]");
        body.ShouldNotContain(key);
    }

    /// <summary>
    /// M14 R11 (request row X10) — the E.164 pattern's BOUNDS, which is the whole pattern: too short is not a
    /// phone number, and a digit run longer than E.164 allows is not one either. Asserted because the failure
    /// mode of a PII pattern is not "it missed one" but "it redacted half of something else and the rest of
    /// the sentence now reads as a phone number".
    /// </summary>
    [Fact]
    public void The_e164_pattern_redacts_a_phone_number_and_leaves_what_is_not_one_alone()
    {
        _redactor.Redact("marcá al +34600111222 y avisá").ShouldContain("[redacted:e164-phone]");

        // Two numbers on one line: both, because the lookarounds only forbid a digit or a `+` next door.
        var two = _redactor.Redact("+34600111222 +34600111223");
        two.ShouldNotContain("600111222", Case.Sensitive);
        two.ShouldNotContain("600111223", Case.Sensitive);
        two.ShouldContain("[redacted:e164-phone]");

        // A country code cannot start with 0 — `+0…` is not E.164.
        _redactor.Redact("+0123456789").ShouldBe("+0123456789");

        // Too short (< 7 digits) and too long (> 15) are both left standing, whole.
        _redactor.Redact("+12345").ShouldBe("+12345");
        _redactor.Redact("+1234567890123456").ShouldBe("+1234567890123456");

        // And a bare digit run with no `+` is not a phone number: nothing in this list claims to find those.
        _redactor.Redact("id 34600111222").ShouldBe("id 34600111222");
    }

    private static List<(string Sample, string Label)> Samples() =>
    [
        ("AKIAIOSFODNN7EXAMPLE", "aws-access-key"),
        ("ASIAIOSFODNN7EXAMPLE", "aws-temp-key"),
        ("aws_secret_access_key=" + new string('A', 40), "aws-secret"),
        ("aws_session_token=" + new string('B', 64), "aws-session"),
        ("-----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY-----", "pem"),
        ("ghp_" + new string('c', 36), "gh-token"),
        ("github_pat_" + new string('d', 64), "gh-pat"),
        ("M" + new string('e', 23) + "." + new string('f', 6) + "." + new string('g', 27), "discord-token"),
        ("sk-ant-" + new string('h', 24), "anthropic-key"),
        ("sk-" + new string('i', 36), "api-key"),
        ("xoxb-" + new string('j', 24), "slack-token"),
        ("eyJ" + new string('k', 24) + "." + new string('l', 24) + "." + new string('m', 24), "jwt"),
        ("password=supersecretvalue", "kv-secret"),
        ("Bearer " + new string('n', 24), "bearer"),
        ("Basic " + new string('o', 20), "basic-auth"),
        ("accessToken=" + new string('p', 20), "json-credential"),
        ("MY_SECRET_TOKEN=" + new string('q', 12), "env-secret"),
        ("ntn_" + new string('r', 32), "notion-token"),
        ("secret_" + new string('s', 32), "notion-legacy-token"),
        ("tskey-" + new string('t', 24), "tailscale-key"),
        ("AIza" + new string('u', 35), "google-api-key"),
        ("O" + new string('v', 23) + "." + new string('w', 6) + "." + new string('x', 27), "discord-token"),
        ("https://user:s3cretpass@example.test", "url-password"),
        ("AKIA" + new string('Y', 16), "aws-access-key"),
        ("-----BEGIN EC PRIVATE KEY-----", "pem-begin"),
        ("+34600111222", "e164-phone"),
    ];
}
