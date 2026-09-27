using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Plugins.Llm.Scripted;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class ScriptedStructuredResponseTests
{
    [Fact]
    public async Task A_consumer_contributes_through_the_plugins_single_provider_and_usage_path()
    {
        var services = Services();
        new ScriptedLlmPlugin().ConfigureServices(services);
        // Added after plugin configuration: the factory must resolve the enumerable at provider creation.
        services.AddSingleton<IScriptedStructuredResponse>(new Response("consumer_read", () => new JsonObject { ["answer"] = 42 }));
        using var sp = services.BuildServiceProvider();

        sp.GetServices<ILlmProvider>().Single().Id.ShouldBe("scripted");
        var structured = sp.GetRequiredService<IStructuredLlm>();
        structured.ShouldBeSameAs(sp.GetRequiredService<ScriptedLlmProvider>());
        var (content, usage) = await ((IStructuredLlmWithUsage)structured).CallWithUsageAsync(
            Request("consumer_read"), TestContext.Current.CancellationToken);
        content!["answer"]!.GetValue<int>().ShouldBe(42);
        usage.ShouldBe(new StructuredLlmUsage(100, 50));
    }

    [Fact]
    public async Task A_handled_null_stops_dispatch_and_registration_order_breaks_ties()
    {
        var services = Services();
        var decline = new Response("unrelated", () => throw new InvalidOperationException("not matched"));
        var first = new Response("emit_plan", () => null);
        var second = new Response("emit_plan", () => throw new InvalidOperationException("shadowed"));
        services.AddSingleton<IScriptedStructuredResponse>(decline);
        services.AddSingleton<IScriptedStructuredResponse>(first);
        services.AddSingleton<IScriptedStructuredResponse>(second);
        new ScriptedLlmPlugin().ConfigureServices(services);
        using var sp = services.BuildServiceProvider();

        (await sp.GetRequiredService<IStructuredLlm>().CallAsync(Request("emit_plan"), TestContext.Current.CancellationToken))
            .ShouldBeNull();
        decline.Calls.ShouldBe(1);
        first.Calls.ShouldBe(1);
        second.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Without_contributions_the_builtin_plan_spec_and_supervisor_still_work()
    {
        var services = Services();
        new ScriptedLlmPlugin().ConfigureServices(services);
        using var sp = services.BuildServiceProvider();
        sp.GetServices<IScriptedStructuredResponse>().ShouldBeEmpty();
        var provider = sp.GetRequiredService<IStructuredLlm>();
        var ct = TestContext.Current.CancellationToken;
        var plan = await provider.CallAsync(Request("emit_plan"), ct);
        plan!["subtasks"]!.AsArray().Count.ShouldBe(3);
        (await provider.CallAsync(Request("emit_spec"), ct)).ShouldBeNull();

        var previous = AgentAmbient.Value;
        try
        {
            AgentAmbient.Value = null;
            var supervisor = await provider.CallAsync(Request("unrecognized_consumer_tool"), ct);
            supervisor!["decision"]!.GetValue<string>().ShouldBe("allow");
            supervisor["confidence"]!.GetValue<double>().ShouldBe(0.9);
        }
        finally
        {
            AgentAmbient.Value = previous;
        }
    }

    [Fact]
    public async Task Cancellation_precedes_contribution_dispatch_and_errors_are_not_silently_answered()
    {
        var contribution = new Response("consumer_read", () => throw new InvalidOperationException("fixture failure"));
        var provider = new ScriptedLlmProvider(new InMemoryCache(new TimeProviderClock(TimeProvider.System)),
            TimeProvider.System, [contribution]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await provider.CallAsync(Request("consumer_read"), cancelled.Token));
        contribution.Calls.ShouldBe(0);
        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await provider.CallAsync(Request("consumer_read"), TestContext.Current.CancellationToken));
        error.Message.ShouldBe("fixture failure");
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICache>(new InMemoryCache(new TimeProviderClock(TimeProvider.System)));
        return services;
    }

    private static StructuredLlmRequest Request(string tool) => new("fixture", [], null,
        new ToolSpec(tool, "fixture", new JsonObject()), new ToolChoiceTool(tool), 256);

    private sealed class Response(string tool, Func<JsonNode?> respond) : IScriptedStructuredResponse
    {
        public int Calls { get; private set; }

        public bool TryRespond(StructuredLlmRequest request, out JsonNode? response)
        {
            Calls++;
            response = null;
            if (request.Tool.Name != tool)
                return false;
            response = respond();
            return true;
        }
    }
}
