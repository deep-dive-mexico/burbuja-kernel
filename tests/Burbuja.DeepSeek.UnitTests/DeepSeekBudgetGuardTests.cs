using System.Net;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Plugins.Llm.DeepSeek;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Burbuja.DeepSeek.UnitTests;

/// <summary>
/// M2 — <b>no stop, no paid provider.</b>
///
/// <para><b>What this closes.</b> The M10 review's R7: <c>Budget:DailyHardStopUsd</c> was checked on the job
/// path (after the run was persisted), on the stateful-agent path and in the AKOS pipeline — and a host that
/// simply never set the key had NO ceiling at all, because <c>0</c> and ABSENT both meant "no stop". The three
/// guards are about whether the day is spent; this is about whether the host was ever allowed to spend, and the
/// one place every paid call must pass through is the provider.</para>
///
/// <para><b>Two statements, two answers.</b> ABSENT is a host that has not decided → the paid provider refuses
/// to serve, and the sentence names the setting. <c>0</c> is a host that decided to spend nothing → nothing is
/// served, which is the neuter that proves the rule is load-bearing. A positive value serves, and the day's
/// ceiling stays the three guards' business — refusing here as well on a spent day would turn a clean PAUSE
/// into a fault.</para>
///
/// <para><b>Both halves are asserted, and the second is the one that matters.</b> The throw is what a caller
/// sees; "no HTTP request was made" is what the owner pays for. A provider that threw AFTER building and
/// sending the request would satisfy the first and bill for the second.</para>
/// </summary>
public sealed class DeepSeekBudgetGuardTests
{
    private static LlmRequest Turn() => ProviderFactory.Request();

    [Fact]
    public async Task A_host_with_no_ceiling_is_not_served_at_all()
    {
        var handler = new ReplayHandler((HttpStatusCode.OK, Recorded.ToolUseResponse, null));
        var provider = ProviderFactory.Create(handler, dailyHardStopUsd: null);

        var thrown = await Should.ThrowAsync<DeepSeekLlmProvider.HardStopUnconfigured>(
            async () => await provider.InvokeAsync(Turn(), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("Budget:DailyHardStopUsd");
        thrown.Message.ShouldContain("no ceiling");
        handler.Calls.ShouldBe(0, "a refusal that still sent the request would still have been billed");
    }

    [Fact]
    public async Task A_ceiling_of_zero_refuses_everything()
    {
        var handler = new ReplayHandler((HttpStatusCode.OK, Recorded.ToolUseResponse, null));
        var provider = ProviderFactory.Create(handler, dailyHardStopUsd: "0");

        var thrown = await Should.ThrowAsync<DeepSeekLlmProvider.HardStopUnconfigured>(
            async () => await provider.InvokeAsync(Turn(), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("0");
        thrown.Message.ShouldContain("spends nothing");
        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The structured path is the SAME path: <c>CallAsync</c> delegates to <c>InvokeAsync</c>, so the guard
    /// cannot be bypassed by a caller that uses the tool-forced shape — which is exactly what the AKOS
    /// pipeline's interpret and judge steps do.
    /// </summary>
    [Fact]
    public async Task The_structured_path_is_guarded_too()
    {
        var handler = new ReplayHandler((HttpStatusCode.OK, Recorded.ToolUseResponse, null));
        var provider = ProviderFactory.Create(handler, dailyHardStopUsd: null);

        await Should.ThrowAsync<DeepSeekLlmProvider.HardStopUnconfigured>(async () =>
            await provider.CallAsync(
                new StructuredLlmRequest(
                    "deepseek-flash",
                    [new InputMessage("user", [new TextBlock("hi")])],
                    "system",
                    new ToolSpec("akos_interpret", "read it", new System.Text.Json.Nodes.JsonObject()),
                    new ToolChoiceTool("akos_interpret"),
                    256),
                TestContext.Current.CancellationToken));

        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_configured_ceiling_serves_the_turn()
    {
        // The control for both neuters above: with a positive ceiling the provider does exactly what it did
        // before this round, so the guard cannot be "refuses everything", which would pass them both.
        var handler = new ReplayHandler((HttpStatusCode.OK, Recorded.ToolUseResponse, null));
        var provider = ProviderFactory.Create(handler, dailyHardStopUsd: "4");

        var response = await provider.InvokeAsync(Turn(), TestContext.Current.CancellationToken);
        response.Blocks.ShouldNotBeEmpty();
        handler.Calls.ShouldBe(1);
    }

    /// <summary>
    /// The plugin SAYS why it will refuse. Refusing to serve is half the rule; the other half is that an
    /// operator can see it in <c>/health/plugins</c> before the first call fails. Degraded maps to HTTP 200, so
    /// a lane's readiness check survives — the plugin is loaded and honest, not broken.
    /// </summary>
    [Fact]
    public async Task The_plugin_reports_the_missing_guard_as_degraded_and_names_the_setting()
    {
        var unset = new DeepSeekBudgetHealth(Config(null));
        var checking = await unset.CheckAsync(TestContext.Current.CancellationToken);
        checking.Status.ShouldBe(PluginHealthStatus.Degraded);
        checking.Description.ShouldNotBeNull().ShouldContain("Budget:DailyHardStopUsd");
        unset.PluginId.ShouldBe("Llm.DeepSeek");

        var zero = await new DeepSeekBudgetHealth(Config("0")).CheckAsync(TestContext.Current.CancellationToken);
        zero.Status.ShouldBe(PluginHealthStatus.Healthy, "zero is a decision, not an omission");

        var limited = await new DeepSeekBudgetHealth(Config("4")).CheckAsync(TestContext.Current.CancellationToken);
        limited.Status.ShouldBe(PluginHealthStatus.Healthy);
        limited.Description.ShouldNotBeNull().ShouldContain("4");
    }

    private static IConfiguration Config(string? stop) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Budget:DailyHardStopUsd"] = stop })
            .Build();
}
