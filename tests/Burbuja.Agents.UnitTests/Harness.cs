using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Plugins.Llm.Scripted;
using Burbuja.Plugins.Policy.Core;
using Burbuja.Plugins.Supervisor;
using Burbuja.Plugins.Tools.Agents;
using Burbuja.Plugins.Tools.Http;
using Burbuja.Plugins.Tools.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

internal static class Harness
{
    public const string WsPath = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/workspaces/demo";
    public const string JobPath = WsPath + "/jobs/j1";

    public static ServiceProvider CreateHost(
        Action<IServiceCollection>? extra = null,
        Dictionary<string, string?>? settings = null,
        IReadOnlyList<IBurbujaPlugin>? plugins = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scenarioDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scenarios"));
        var values = new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "all",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Kernel:TrustedPlugins:0"] = "Policy.Core",
            ["Kernel:TrustedPlugins:1"] = "Supervisor",
            ["Kernel:NodeId"] = "node-a",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Auth:ApiKeys:1:Key"] = "reader-key",
            ["Auth:ApiKeys:1:PrincipalId"] = "reader",
            ["Auth:ApiKeys:1:Roles:0"] = "Reader",
            ["Runtime:OutboxRelayInterval"] = "00:00:00.050",
            ["Runtime:MaxTurns"] = "80",
            ["Budget:DefaultJobUsd"] = "5",
            ["Arm:Throttle:WritesPerMinute"] = "12000",
            ["Arm:Throttle:ReadsPerMinute"] = "12000",
            ["Arm:Throttle:DeletesPerMinute"] = "12000",
            ["Agents:ScenarioDirectory"] = scenarioDir,
            ["Agents:ProviderId"] = "scripted",
            // W1: the key the workspace initializer's own ARM calls run as. The harness's owner key, because a
            // test host has one principal and the alternative is a second auth entry that proves nothing.
            ["Agents:LifecycleKey"] = "owner-key",
            ["Compute:LocalDirectory:Root"] = Path.Combine(Path.GetTempPath(), "burbuja-lifecycle-" + Guid.NewGuid().ToString("N")),
            ["Compute:Git:Root"] = Path.Combine(Path.GetTempPath(), "burbuja-lifecycle-git-" + Guid.NewGuid().ToString("N")),
        };
        if (settings is not null)
        {
            foreach (var kv in settings)
                values[kv.Key] = kv.Value;
        }

        Environment.SetEnvironmentVariable("BURBUJA_SCRIPTED_SCENARIO_DIR", scenarioDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        var registered = new List<IBurbujaPlugin>
        {
            new AgentsPlugin(),
            new PolicyCorePlugin(),
            new ScriptedLlmPlugin(),
            new SupervisorPlugin(),
            new WorkspaceToolsPlugin(),
            new HttpToolsPlugin(),
            new AgentToolsPlugin(),
        };
        if (plugins is not null)
            registered.AddRange(plugins);
        services.AddBurbujaCore(config, [.. registered]);
        extra?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public static async Task SeedAsync(IServiceProvider sp)
    {
        (await Send(sp, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}""")).Ok();
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", """{"properties":{}}""")).Ok();
        (await Send(sp, "PUT", WsPath, """{"properties":{"displayName":"demo","repoId":"demo","profile":"DevFullAuto"}}""")).Ok();
    }

    public static string JobBody(string scenario, string? extraSpec = null, string language = "es", string? modelId = null)
    {
        var spec = extraSpec ?? "{\"kind\":\"implement\",\"title\":\"t1\",\"slug\":\"job\",\"brief\":\"do the work\",\"language\":\"" + language + "\",\"targetEnv\":\"dev\",\"autoAccept\":true}";
        var model = modelId is null ? "" : ",\"modelId\":\"" + modelId + "\"";
        return "{\"tags\":{\"" + AgentsConstants.ScenarioTag + "\":\"" + scenario + "\"},\"properties\":{\"spec\":" + spec + model + "}}";
    }

    public static async Task<ArmRequestContext> Send(
        IServiceProvider sp,
        string method,
        string path,
        string? body = null,
        string key = "owner-key",
        Dictionary<string, string>? extraHeaders = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = key };
        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
                headers[kv.Key] = kv.Value;
        }

        Stream? stream = body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body));
        var ctx = new ArmRequestContext
        {
            Method = method,
            Path = path,
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = headers,
            Body = stream,
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, TestContext.Current.CancellationToken);
        return ctx;
    }

    public static async Task RunJobAsync(IServiceProvider sp, string jobPath, CancellationToken ct)
    {
        var orchestrator = sp.GetRequiredService<JobOrchestrator>();
        await orchestrator.StartAsync(jobPath.ToLowerInvariant(), ct);
    }

    public static void Ok(this ArmRequestContext ctx)
    {
        ctx.Error?.Code.ShouldBeNull($"{ctx.StatusCode} {ctx.Error?.Code} {ctx.Error?.Message}");
        (ctx.StatusCode is >= 200 and < 300).ShouldBeTrue($"{ctx.StatusCode}");
    }

    public static JsonObject AsJson(this ArmRequestContext ctx)
    {
        if (ctx.Result is JsonObject o)
            return o;
        return JsonSerializer.SerializeToNode(ctx.Result, JsonWire.Options)!.AsObject();
    }
}
