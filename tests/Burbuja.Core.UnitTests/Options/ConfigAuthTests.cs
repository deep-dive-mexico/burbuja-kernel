using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Options;

[Collection(EnvironmentCollection.Name)]
public sealed class ConfigAuthTests
{
    [Fact]
    public void BURBUJA_API_KEY_loads_without_throwing_and_seeds_owner()
    {
        var previous = Environment.GetEnvironmentVariable("BURBUJA_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("BURBUJA_API_KEY", "env-api-key");
            var cfg = new ConfigurationBuilder()
                .Add(new EnvAliasConfigurationSource())
                .Build();
            cfg["Auth:ApiKeys:0:Key"].ShouldBe("env-api-key");
            cfg["Auth:ApiKeys:0:PrincipalId"].ShouldBe("owner");
            cfg["Auth:ApiKeys:0:Roles:0"].ShouldBe("Owner");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BURBUJA_API_KEY", previous);
        }
    }

    [Fact]
    public void KernelOptions_NodeId_falls_back_to_HOSTNAME_on_the_bound_options()
    {
        var previous = Environment.GetEnvironmentVariable("HOSTNAME");
        try
        {
            Environment.SetEnvironmentVariable("HOSTNAME", "node-from-hostname");
            using var sp = CreateHost(nodeId: "");
            sp.GetRequiredService<IOptions<KernelOptions>>().Value.NodeId.ShouldBe("node-from-hostname");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOSTNAME", previous);
        }
    }

    [Fact]
    public void KernelOptions_explicit_NodeId_is_not_overwritten()
    {
        using var sp = CreateHost(nodeId: "explicit-node");
        sp.GetRequiredService<IOptions<KernelOptions>>().Value.NodeId.ShouldBe("explicit-node");
    }

    [Fact]
    public void Budget_Llm_Workspace_options_bind()
    {
        using var sp = CreateHost(extra: new Dictionary<string, string?>
        {
            ["Budget:DailySoftCapUsd"] = "10",
            ["Budget:DailyHardStopUsd"] = "20",
            ["Budget:DefaultJobUsd"] = "5",
            ["Llm:ProviderId"] = "scripted",
            ["Llm:ArbiterProviderId"] = "scripted",
            ["Workspace:WorktreeRoot"] = "/tmp/work",
            ["Workspace:RepoRoot"] = "/tmp/repo",
        });
        var budget = sp.GetRequiredService<IOptions<BudgetOptions>>().Value;
        budget.DailySoftCapUsd.ShouldBe(10);
        budget.DailyHardStopUsd.ShouldBe(20);
        budget.DefaultJobUsd.ShouldBe(5);
        sp.GetRequiredService<IOptions<LlmOptions>>().Value.ProviderId.ShouldBe("scripted");
        sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value.WorktreeRoot.ShouldBe("/tmp/work");
    }

    [Fact]
    public void Empty_Kernel_Role_fails_options_validation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:NodeId"] = "n",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        using var sp = services.BuildServiceProvider();
        Should.Throw<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<KernelOptions>>().Value);
    }

    [Fact]
    public void Empty_api_key_fails_options_validation()
    {
        using var sp = CreateHost(extra: new Dictionary<string, string?>
        {
            ["Auth:ApiKeys:0:Key"] = "",
        });
        Should.Throw<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<AuthOptions>>().Value);
    }

    [Fact]
    public void Zero_worker_concurrency_fails_options_validation()
    {
        using var sp = CreateHost(extra: new Dictionary<string, string?>
        {
            ["Runtime:WorkerConcurrency"] = "0",
        });
        Should.Throw<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value);
    }

    [Fact]
    public void Zero_arm_writes_per_minute_fails_options_validation()
    {
        using var sp = CreateHost(extra: new Dictionary<string, string?>
        {
            ["Arm:Throttle:WritesPerMinute"] = "0",
        });
        Should.Throw<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<ArmOptions>>().Value);
    }

    [Fact]
    public async Task Missing_api_key_returns_401_with_WWW_Authenticate()
    {
        using var sp = CreateHost();
        var ctx = new ArmRequestContext
        {
            Method = "GET",
            Path = "/subscriptions",
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string>(),
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        ctx.StatusCode.ShouldBe(401);
        ctx.Error!.Code.ShouldBe("AuthenticationFailed");
        ctx.ResponseHeaders["WWW-Authenticate"].ShouldBe("Bearer, ApiKey");
    }

    [Fact]
    public async Task Principal_groups_are_populated_from_the_api_key_entry()
    {
        using var sp = CreateHost(extra: new Dictionary<string, string?>
        {
            ["Auth:ApiKeys:0:Groups:0"] = "ops",
            ["Auth:ApiKeys:0:Groups:1"] = "platform",
        });
        var ctx = new ArmRequestContext
        {
            Method = "GET",
            Path = "/subscriptions",
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string> { ["x-api-key"] = "owner-key" },
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        ctx.StatusCode.ShouldBe(200);
        ctx.Principal.ShouldNotBeNull();
        ctx.Principal!.Groups.ShouldBe(["ops", "platform"]);
    }

    [Fact]
    public void FixedTimeKeyEquals_accepts_the_configured_key()
    {
        AuthenticationMiddleware.FixedTimeKeyEquals("owner-key", "owner-key").ShouldBeTrue();
        AuthenticationMiddleware.FixedTimeKeyEquals("owner-key", "other-key").ShouldBeFalse();
        AuthenticationMiddleware.FixedTimeKeyEquals("owner-key", null).ShouldBeFalse();
        AuthenticationMiddleware.FixedTimeKeyEquals("owner-key", "").ShouldBeFalse();
    }

    private static ServiceProvider CreateHost(string nodeId = "test-node", Dictionary<string, string?>? extra = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var data = new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Kernel:NodeId"] = nodeId,
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
                data[kv.Key] = kv.Value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
