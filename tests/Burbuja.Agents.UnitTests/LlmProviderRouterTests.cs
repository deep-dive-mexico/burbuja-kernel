using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// Staging a second llm.provider must not silently change who serves every turn: the runtime picks
/// by <c>Agents:ProviderId</c>, and a name that is not installed is a boot failure.
/// </summary>
public sealed class LlmProviderRouterTests
{
    [Fact]
    public void Named_provider_wins_over_registration_order()
    {
        var router = new LlmProviderRouter([new Fake("deepseek"), new Fake("scripted")], Options("scripted"));

        router.Provider.Id.ShouldBe("scripted");
        router.Installed.ShouldBe(["deepseek", "scripted"]);
    }

    /// <summary>
    /// The placeholder is not an INSTALLED provider.
    ///
    /// <para><b>Found by the AKOS roster console.</b> Its availability column is computed from the installed
    /// provider list, and the host answered <c>["llm.deepseek", "null", "scripted"]</c> — because
    /// <c>NullLlmProvider</c> (the router's own fallback, which cannot serve anything) was registered as an
    /// <c>ILlmProvider</c>. A host whose only "provider" was the placeholder would have been reported as able
    /// to embody a persona, and the evidence line would have named it. The list a reader trusts has to be the
    /// list of things that can serve.</para>
    /// </summary>
    [Fact]
    public void The_placeholder_is_not_one_of_the_installed_providers()
    {
        var host = Harness.CreateHost();

        var installed = host.GetServices<ILlmProvider>().Select(p => p.Id).ToList();

        installed.ShouldNotContain("null", "NullLlmProvider is the router's fallback, not an installed provider");
        installed.ShouldNotContain(p => string.IsNullOrEmpty(p), customMessage: "and nothing without an id is a provider");
        installed.ShouldContain("scripted", "the fixture's own provider is installed, so this is not an empty list");

        // The fallback still exists where it belongs: the router hands one out when nothing is installed.
        new LlmProviderRouter([], Options("")).Provider.ShouldBeOfType<NullLlmProvider>();
    }

    [Fact]
    public void Empty_id_keeps_the_last_registration()
    {
        var router = new LlmProviderRouter([new Fake("scripted"), new Fake("deepseek")], Options(""));

        router.Provider.Id.ShouldBe("deepseek");
    }

    [Fact]
    public void Unknown_id_fails_loud()
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => new LlmProviderRouter([new Fake("scripted")], Options("bedrock")));

        thrown.Message.ShouldContain("bedrock");
        thrown.Message.ShouldContain("scripted");
    }

    [Fact]
    public void No_provider_at_all_is_the_null_one()
    {
        new LlmProviderRouter([], Options("")).Provider.ShouldBeOfType<NullLlmProvider>();
    }

    private static IOptions<AgentsOptions> Options(string providerId) =>
        Microsoft.Extensions.Options.Options.Create(new AgentsOptions { ProviderId = providerId });

    private sealed class Fake(string id) : ILlmProvider
    {
        public string Id { get; } = id;

        public ModelCapabilities Capabilities(string modelId) => new(false, "disabled", true, true, 1024, 200_000);

        public ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
