using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Deployments;
using Burbuja.Plugins.Policy.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

internal static class Harness
{
    public const string EnvPath = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Deployments/environments/dev-inmem";
    public const string DepPath = EnvPath + "/deployments/d1";

    public static ServiceProvider CreateHost(bool startWorkers = false, string role = "all", Action<IServiceCollection>? extra = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var workRoot = Path.Combine(Path.GetTempPath(), "burbuja-deployments-tests");
        Directory.CreateDirectory(workRoot);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = role,
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Auth:ApiKeys:1:Key"] = "reader-key",
            ["Auth:ApiKeys:1:PrincipalId"] = "reader",
            ["Auth:ApiKeys:1:Roles:0"] = "Reader",
            ["Runtime:OutboxRelayInterval"] = "00:00:00.050",
            ["Arm:Throttle:WritesPerMinute"] = "12000",
            ["Arm:Throttle:ReadsPerMinute"] = "12000",
            ["Arm:Throttle:DeletesPerMinute"] = "12000",
            ["Deployments:LocalProcess:Root"] = workRoot,
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config, [new DeploymentsPlugin(), new PolicyCorePlugin()]);
        extra?.Invoke(services);
        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        if (startWorkers)
        {
            foreach (var hs in sp.GetServices<IHostedService>())
                hs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return sp;
    }

    public static async Task SeedAsync(IServiceProvider sp)
    {
        (await Send(sp, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}""")).Ok();
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", """{"properties":{}}""")).Ok();
        (await Send(sp, "PUT", EnvPath, """{"properties":{"displayName":"inmem"}}""")).Ok();
    }

    public static string Template(params string[] names) =>
        Template(names.Select(n => (n, """{"_test":{}}""")).ToArray());

    public static string Template(params (string name, string spec)[] resources)
    {
        var items = resources.Select(r => $$"""{"name":"{{r.name}}","kind":"process","spec":{{r.spec}}}""");
        return """{"properties":{"template":{"schema":"burbuja.deployments/v1","resources":[""" + string.Join(',', items) + "]}}}";
    }

    public static async Task<ArmRequestContext> Send(
        IServiceProvider sp,
        string method,
        string path,
        string? body = null,
        string key = "owner-key",
        Dictionary<string, string>? extraHeaders = null,
        Dictionary<string, string>? query = null)
    {
        query ??= new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion };
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
            Query = query,
            Headers = headers,
            Body = stream,
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, TestContext.Current.CancellationToken);
        return ctx;
    }

    public static async Task RunDeploymentAsync(IServiceProvider sp, string deploymentPath, CancellationToken ct)
    {
        var runner = sp.GetRequiredService<Burbuja.Deployments.Execution.DeploymentRunner>();
        var store = sp.GetRequiredService<IResourceStore>();
        var envRaw = await store.GetAsync(EnvPath.ToLowerInvariant(), ct)
            ?? throw new InvalidOperationException("environment missing");
        var environment = envRaw.Properties.Deserialize<Burbuja.Deployments.Abstractions.EnvironmentProperties>(JsonWire.Options)
                          ?? new Burbuja.Deployments.Abstractions.EnvironmentProperties();
        await runner.RunAsync(deploymentPath.ToLowerInvariant(), envRaw.Id, environment, ct);
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
