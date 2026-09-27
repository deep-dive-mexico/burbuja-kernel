using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Compute;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Options;
using Burbuja.Plugins.Policy.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Compute.UnitTests;

/// <summary>Spy process runner: records every spawn so a policy denial can be proven to never run.</summary>
public sealed class SpyProcessRunner : IProcessRunner
{
    public List<ProcessStartRequest> Calls { get; } = [];

    public Func<ProcessStartRequest, ProcessResult>? Result { get; set; }

    public ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct)
    {
        Calls.Add(request);
        return new(Result?.Invoke(request) ?? new ProcessResult(0, "ok", "", false));
    }
}

public static class Harness
{
    public const string OwnerKey = "owner-key";

    public static (ServiceProvider Services, SpyProcessRunner Processes) CreateHost(string? computeRoot = null, bool spyProcesses = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-compute-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var root = computeRoot ?? Path.Combine(Path.GetTempPath(), "burbuja-compute-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "all",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Kernel:TrustedPlugins:0"] = "Policy.Core",
            ["Kernel:TrustedPlugins:1"] = "Supervisor",
            ["Kernel:TrustedPlugins:2"] = "Burbuja.Compute",
            ["Auth:ApiKeys:0:Key"] = OwnerKey,
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:PrincipalType"] = "User",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Compute:LocalDirectory:Root"] = root,
            ["Compute:Git:Root"] = Path.Combine(root, "git"),
        }).Build();

        var services = new ServiceCollection();
        services.AddBurbujaCore(config, [new ComputePlugin(), new PolicyCorePlugin()]);
        var spy = new SpyProcessRunner();
        if (spyProcesses)
            services.AddSingleton<IProcessRunner>(spy);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        return (provider, spy);
    }

    public static async Task<ArmRequestContext> Send(
        IServiceProvider sp,
        string method,
        string path,
        string? body = null,
        string key = OwnerKey,
        string? clientRequestId = null)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["api-version"] = ArmConstants.ApiVersion };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = key };
        if (clientRequestId is not null)
            headers["x-ms-client-request-id"] = clientRequestId;
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

    public static async Task SeedAsync(IServiceProvider sp)
    {
        (await Send(sp, "PUT", "/subscriptions/sub1", """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
    }

    public static string WorkspacePath(string name) =>
        $"/subscriptions/sub1/resourceGroups/rg1/providers/{ComputeConstants.Namespace}/workspaces/{name}";

    public static string Spec(string backend = "local-directory", string image = "ubuntu-24.04", string size = "small", string extra = "") =>
        "{\"properties\":{\"displayName\":\"w\",\"backend\":\"" + backend
        + "\",\"image\":\"" + image
        + "\",\"size\":\"" + size + "\"" + extra + "}}";
}
