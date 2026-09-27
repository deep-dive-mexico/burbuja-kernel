using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

/// <summary>
/// C3 (N4): the tenancy catalog and idempotency keys live behind seams, and the catalog is
/// rebuilt from the resource store on boot (durability by rehydration, not a second table).
/// </summary>
public sealed class DirectoryRehydrationTests
{
    [Fact]
    public async Task Rehydrator_restores_subscriptions_rgs_and_registrations_from_the_store()
    {
        using var host1 = CreateHost();
        await Send(host1, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}""");
        await Send(host1, "PUT", "/subscriptions/sub1/resourceGroups/rg1", """{"properties":{}}""");
        (await Send(host1, "POST", "/subscriptions/sub1/providers/Burbuja.Authorization/register")).StatusCode.ShouldBe(200);

        var store = host1.GetRequiredService<IResourceStore>();
        using var host2 = CreateHost(store);
        await host2.GetRequiredService<ArmDirectoryRehydrator>().StartAsync(TestContext.Current.CancellationToken);

        (await Send(host2, "GET", "/subscriptions/sub1")).StatusCode.ShouldBe(200);
        (await Send(host2, "GET", "/subscriptions/sub1/resourceGroups/rg1")).StatusCode.ShouldBe(200);
        var directory = host2.GetRequiredService<IArmDirectory>();
        directory.TryGetProviderRegistration("sub1", "Burbuja.Authorization", out var state).ShouldBeTrue();
        state.ShouldBe(ProviderRegistrationState.Registered);
    }

    [Fact]
    public async Task A_second_container_without_rehydration_reports_SubscriptionNotFound()
    {
        using var host1 = CreateHost();
        await Send(host1, "PUT", "/subscriptions/sub1", """{"properties":{}}""");
        var store = host1.GetRequiredService<IResourceStore>();

        using var host2 = CreateHost(store);
        var get = await Send(host2, "GET", "/subscriptions/sub1");
        get.StatusCode.ShouldBe(404);
        get.Error!.Code.ShouldBe("SubscriptionNotFound");
    }

    [Fact]
    public void Rehydrator_runs_before_a_host_added_seed_service()
    {
        var services = new ServiceCollection();
        services.AddBurbujaCore(Config());
        services.AddSingleton<IHostedService>(new FakeSeedService());
        using var sp = services.BuildServiceProvider();

        var hosted = sp.GetServices<IHostedService>().ToList();
        var rehydratorIndex = hosted.FindIndex(h => h is ArmDirectoryRehydrator);
        var seedIndex = hosted.FindIndex(h => h is FakeSeedService);
        rehydratorIndex.ShouldBeGreaterThanOrEqualTo(0);
        seedIndex.ShouldBeGreaterThan(rehydratorIndex);
    }

    [Fact]
    public void Idempotency_begin_is_exclusive_and_get_expires()
    {
        var clock = new MutableClock();
        IIdempotencyStore store = new InMemoryIdempotencyStore(clock);

        store.TryBegin("k").ShouldBeTrue();
        store.TryBegin("k").ShouldBeFalse();
        store.End("k");
        store.TryBegin("k").ShouldBeTrue();

        store.Put("k", new IdempotencyRecord(
            "hash",
            JsonNode.Parse("""{"ok":true}"""),
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            clock.UtcNow.AddMinutes(1)));

        store.Get("k").ShouldNotBeNull();
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        store.Get("k").ShouldBeNull();
    }

    [Fact]
    public void Subscription_delete_cascade_removes_resource_groups()
    {
        // Subscriptions are not DELETE-able over ARM (405: no Delete capability), so the cascade
        // SubscriptionHandler.DeleteAsync performs is proven at the directory seam.
        IArmDirectory directory = new InMemoryArmDirectory();
        directory.UpsertSubscription(new SubscriptionRecord { Id = "sub1" });
        directory.UpsertResourceGroup(new ResourceGroupRecord { SubscriptionId = "sub1", Name = "rg1" });
        directory.UpsertResourceGroup(new ResourceGroupRecord { SubscriptionId = "sub1", Name = "rg2" });
        directory.UpsertResourceGroup(new ResourceGroupRecord { SubscriptionId = "other", Name = "rg3" });

        directory.RemoveSubscription("sub1").ShouldBeTrue();
        directory.RemoveResourceGroupsFor("sub1");

        directory.TryGetSubscription("sub1", out _).ShouldBeFalse();
        directory.TryGetResourceGroup("sub1", "rg1", out _).ShouldBeFalse();
        directory.TryGetResourceGroup("sub1", "rg2", out _).ShouldBeFalse();
        directory.TryGetResourceGroup("other", "rg3", out _).ShouldBeTrue();
    }

    [Fact]
    public void In_memory_directory_satisfies_the_interface()
    {
        // Two implementations must be interchangeable: the seam is the interface, not the class.
        IArmDirectory directory = new InMemoryArmDirectory();
        directory.UpsertSubscription(new SubscriptionRecord { Id = "s1", DisplayName = "one" });
        directory.UpsertResourceGroup(new ResourceGroupRecord { SubscriptionId = "s1", Name = "rg" });
        directory.SetProviderRegistration("s1", "Burbuja.Agents", ProviderRegistrationState.Unregistered);

        directory.TryGetSubscription("s1", out var sub).ShouldBeTrue();
        sub.DisplayName.ShouldBe("one");
        directory.TryGetResourceGroup("s1", "rg", out _).ShouldBeTrue();
        directory.TryGetProviderRegistration("s1", "Burbuja.Agents", out var state).ShouldBeTrue();
        state.ShouldBe(ProviderRegistrationState.Unregistered);

        directory.RemoveSubscription("s1").ShouldBeTrue();
        directory.TryGetSubscription("s1", out _).ShouldBeFalse();
    }

    private static async Task<ArmRequestContext> Send(
        IServiceProvider sp,
        string method,
        string path,
        string? body = null)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api-version"] = ArmConstants.ApiVersion,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = "owner-key",
        };
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

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = TempPluginsDir(),
            ["Kernel:EnabledPlugins"] = "",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
        }).Build();

    private static ServiceProvider CreateHost(IResourceStore? sharedStore = null)
    {
        var services = new ServiceCollection();
        services.AddBurbujaCore(Config());
        if (sharedStore is not null)
            services.AddSingleton(sharedStore);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static string TempPluginsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-dir-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class FakeSeedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
