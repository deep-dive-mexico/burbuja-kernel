using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Clock;
using Burbuja.Core.Process;
using Burbuja.Plugins.Llm.DeepSeek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.DeepSeek.UnitTests;

public sealed class PluginValidationTests
{
    [Fact]
    public void Unpriced_model_fails_validate_on_start()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DeepSeek:ModelId"] = "gpt-4" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        new DeepSeekPlugin().ConfigureServices(services);
        using var sp = services.BuildServiceProvider();

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(
            () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeepSeekOptions>>().Value);
    }
}

public sealed class PricebookTests
{
    private static readonly DateTimeOffset OffPeak = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero); // Wednesday noon
    private static readonly DateTimeOffset Peak = new(2026, 9, 9, 2, 0, 0, TimeSpan.Zero);    // Wednesday 02:00

    [Fact]
    public void Off_peak_and_peak_quotes_differ_by_exactly_two_times()
    {
        var book = new DeepSeekPricebook();
        var usage = new Usage(1_000_000, 1_000_000, 0, 0);

        var offPeak = book.Quote("deepseek-flash", usage, OffPeak);
        var peak = book.Quote("deepseek-flash", usage, Peak);

        offPeak.ShouldBe(0.825m);
        peak.ShouldBe(1.65m);
        (peak / offPeak).ShouldBe(2m);
    }

    [Fact]
    public void Peak_window_excludes_weekends()
    {
        var book = new DeepSeekPricebook();
        var saturday = new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 9, 13, 7, 0, 0, TimeSpan.Zero);

        book.IsPeak(saturday).ShouldBeFalse();
        book.IsPeak(sunday).ShouldBeFalse();
        book.IsPeak(Peak).ShouldBeTrue();
        book.IsPeak(new DateTimeOffset(2026, 9, 9, 7, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        book.IsPeak(new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void Cache_write_tokens_are_billed_at_cache_miss()
    {
        var book = new DeepSeekPricebook();
        var asInput = book.Quote("deepseek-flash", new Usage(1_000_000, 0, 0, 0), OffPeak);
        var asWrite = book.Quote("deepseek-flash", new Usage(0, 0, 0, 1_000_000), OffPeak);
        var asRead = book.Quote("deepseek-flash", new Usage(0, 0, 1_000_000, 0), OffPeak);

        asWrite.ShouldBe(asInput);
        asRead.ShouldBeLessThan(asWrite);
    }

    [Fact]
    public void Unknown_model_is_not_priced()
    {
        var book = new DeepSeekPricebook();
        book.Knows("deepseek-flash").ShouldBeTrue();
        book.Knows("gpt-4").ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => book.Quote("gpt-4", new Usage(1, 1, 0, 0), OffPeak));
    }
}

public sealed class ContractRoundTripTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void Image_block_round_trips()
    {
        ContentBlock block = new ImageBlock(new ImageSource("base64", "image/png", "AAAA"));
        var json = JsonSerializer.Serialize(block, typeof(ContentBlock));
        json.ShouldContain("\"type\":\"image\"");
        json.ShouldContain("\"media_type\":\"image/png\"");
        RoundTrip(block).ShouldBe(block);
    }

    [Fact]
    public void Thinking_block_round_trips_signature()
    {
        ContentBlock block = new ThinkingBlock("reasoning", "sig-1");
        var json = JsonSerializer.Serialize(block, typeof(ContentBlock));
        json.ShouldContain("\"signature\":\"sig-1\"");
        RoundTrip(block).ShouldBe(block);

        RoundTrip<ContentBlock>(new ThinkingBlock("no signature")).ShouldBe(new ThinkingBlock("no signature"));
    }

    [Fact]
    public void Tool_choice_none_and_any_round_trip()
    {
        var any = JsonSerializer.Serialize<ToolChoice>(new ToolChoiceAny());
        any.ShouldContain("\"type\":\"any\"");
        JsonSerializer.Deserialize<ToolChoice>(any).ShouldBeOfType<ToolChoiceAny>();

        var none = JsonSerializer.Serialize<ToolChoice>(new ToolChoiceNone());
        none.ShouldContain("\"type\":\"none\"");
        JsonSerializer.Deserialize<ToolChoice>(none).ShouldBeOfType<ToolChoiceNone>();
    }

    [Fact]
    public void Request_effort_defaults_to_null()
    {
        var request = new LlmRequest("m", [new InputMessage("user", [new TextBlock("x")])], null, null, null, 10, null);
        request.Effort.ShouldBeNull();
    }
}

public sealed class KeychainResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keychain_resolver_uses_a_minted_grant_and_caches()
    {
        var script = Path.Combine(Path.GetTempPath(), "burbuja-key-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho sk-test-key\n", Ct);
        var spy = new SpyProcessRunner("sk-test-key");
        var resolver = new KeychainSecretResolver(
            spy,
            new ExecutionGrantAuthority(new TimeProviderClock(TimeProvider.System)),
            Microsoft.Extensions.Options.Options.Create(new DeepSeekOptions { ApiKeyCommand = script }));

        (await resolver.ResolveAsync("deepseek", Ct)).ShouldBe("sk-test-key");
        spy.Calls.Count.ShouldBe(1);

        (await resolver.ResolveAsync("deepseek", Ct)).ShouldBe("sk-test-key");
        spy.Calls.Count.ShouldBe(1, "the resolved key is cached for ten minutes");
    }

    /// <summary>
    /// A name this resolver does not own resolves to null and **runs nothing**.
    ///
    /// <para>The seam is name-based, so the only correct answer for a name it does not serve is "not mine".
    /// This resolver runs one command that prints the DeepSeek key, and before this check it answered for
    /// every name — which meant the first other component to ask for a credential (M8/I1's mailbox, asking
    /// for `graph-mail`) would have been handed the DeepSeek key. The spy asserts the command did not even
    /// run: a refusal that still executed the command would be a refusal in name only.</para>
    /// </summary>
    [Fact]
    public async Task A_name_it_does_not_own_resolves_to_null_without_running_the_command()
    {
        var script = Path.Combine(Path.GetTempPath(), "burbuja-key-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho sk-test-key\n", Ct);
        var spy = new SpyProcessRunner("sk-test-key");
        var resolver = new KeychainSecretResolver(
            spy,
            new ExecutionGrantAuthority(new TimeProviderClock(TimeProvider.System)),
            Microsoft.Extensions.Options.Options.Create(new DeepSeekOptions { ApiKeyCommand = script }));

        (await resolver.ResolveAsync("graph-mail", Ct)).ShouldBeNull();
        spy.Calls.ShouldBeEmpty("another component's secret name must not run this command");

        // And the name it does own still works, so the check is a scope rather than a break.
        (await resolver.ResolveAsync("deepseek", Ct)).ShouldBe("sk-test-key");
        spy.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_key_command_resolves_to_null()
    {
        var resolver = new KeychainSecretResolver(
            new SpyProcessRunner(),
            new ExecutionGrantAuthority(new TimeProviderClock(TimeProvider.System)),
            Microsoft.Extensions.Options.Options.Create(new DeepSeekOptions { ApiKeyCommand = "/nonexistent/deepseek-key" }));

        (await resolver.ResolveAsync("deepseek", Ct)).ShouldBeNull();
    }

    [Fact]
    public void Missing_key_command_is_not_registered()
    {
        KeychainSecretResolver.CommandExists("/nonexistent/deepseek-key").ShouldBeFalse();
        KeychainSecretResolver.CommandExists(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).ShouldBeFalse();
    }
}

/// <summary>
/// No live DeepSeek lane runs this iteration (the trait is reserved). These facts exist so the
/// layer shows up as skipped in TRX rather than being silently absent.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveDeepSeekTests
{
    [Fact]
    public void Live_deepseek_flash_answers()
    {
        Assert.Skip("reserved: set BURBUJA_LIVE=1 and provide a key to run the live DeepSeek lane");
    }
}
