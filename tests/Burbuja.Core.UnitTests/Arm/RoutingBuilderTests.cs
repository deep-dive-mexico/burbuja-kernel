using System.Text;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.Arm.Rps;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

public sealed class RoutingBuilderTests
{
    [Fact]
    public void Descriptor_rejects_actions_without_actions_capability()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DescriptorBuilder.For("x")
                .Properties<LockProperties>()
                .Capabilities(ResourceCapabilities.Get)
                .Actions(new ResourceActionDescriptor("go", false))
                .Build());
        ex.Message.ShouldContain("Actions capability");
    }

    [Fact]
    public void Descriptor_rejects_async_delete_without_delete()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DescriptorBuilder.For("x").Properties<LockProperties>().AsyncDelete().Build());
        ex.Message.ShouldContain("IsAsyncDelete");
    }

    [Fact]
    public void Descriptor_rejects_data_plane_action_collision()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DescriptorBuilder.For("jobs")
                .Properties<LockProperties>()
                .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Actions)
                .Actions(new ResourceActionDescriptor("events", false))
                .DataPlane(new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents))
                .Build());
        ex.Message.ShouldContain("collides");
    }

    [Fact]
    public void Descriptor_rejects_async_put_without_put()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DescriptorBuilder.For("x").Properties<LockProperties>().AsyncPut().Build());
        ex.Message.ShouldContain("IsAsyncPut");
    }

    [Fact]
    public void Descriptor_rejects_data_plane_child_collision()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DescriptorBuilder.For("workspaces")
                .Properties<LockProperties>()
                .Capabilities(ResourceCapabilities.Get)
                .Children("jobs")
                .DataPlane(new DataPlaneRouteDescriptor("jobs", DataPlaneKind.ServerSentEvents))
                .Build());
        ex.Message.ShouldContain("collides");
    }

    [Fact]
    public void Missing_child_type_fails_table_build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IResourceProvider, BrokenChildrenProvider>();
        services.AddResourceType<LockProperties, StubLockHandler>("Burbuja.Test", "workspaces");
        var sp = services.BuildServiceProvider();
        var ex = Should.Throw<PluginBootException>(() => ArmRoutingTableBuilder.Build(sp));
        ex.Errors.ShouldContain(e => e.Contains("child 'jobs' does not exist"));
    }

    [Fact]
    public void Missing_handler_fails_table_build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IResourceProvider, ResourcesProvider>();
        var sp = services.BuildServiceProvider();
        var ex = Should.Throw<PluginBootException>(() => ArmRoutingTableBuilder.Build(sp));
        ex.Errors.ShouldContain(e => e.Contains("missing IResourceHandler"));
    }

    [Fact]
    public async Task Child_handler_404s_when_parent_missing_from_store()
    {
        using var sp = CreateHost();
        await Seed(sp);
        var dir = sp.GetRequiredService<IArmDirectory>();
        dir.UpsertResourceGroup(new ResourceGroupRecord
        {
            SubscriptionId = "sub1",
            Name = "ghost",
        });
        var ctx = new ArmRequestContext
        {
            Method = "PUT",
            Path = "/subscriptions/sub1/resourceGroups/ghost/providers/Burbuja.Authorization/locks/l1",
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string> { ["x-api-key"] = "owner-key" },
            Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"properties":{"level":"CanNotDelete"}}""")),
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        ctx.StatusCode.ShouldBe(404);
    }

    private static async Task Seed(ServiceProvider sp)
    {
        var ctx = new ArmRequestContext
        {
            Method = "PUT",
            Path = "/subscriptions/sub1",
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string> { ["x-api-key"] = "owner-key" },
            Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"properties":{"displayName":"s","state":"Enabled"}}""")),
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        ctx.StatusCode.ShouldBeOneOf(200, 201);
    }

    private static ServiceProvider CreateHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private sealed class BrokenChildrenProvider : IResourceProvider
    {
        public string Namespace => "Burbuja.Test";
        public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; } =
        [
            DescriptorBuilder.For("workspaces")
                .Properties<LockProperties>()
                .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put)
                .Children("jobs")
                .Build(),
        ];

        public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;
    }

    private sealed class StubLockHandler : IResourceHandler<LockProperties>
    {
        public ValueTask<ResourceEnvelope<LockProperties>?> GetAsync(ResourceRequest req, CancellationToken ct) => default;
        public ValueTask<PutOutcome<LockProperties>> PutAsync(ResourceRequest req, LockProperties desired, CancellationToken ct) =>
            new(new PutOutcome<LockProperties>.Rejected(ArmError.Validation("stub", [])));
        public ValueTask<PutOutcome<LockProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
            new(new PutOutcome<LockProperties>.Rejected(ArmError.Validation("stub", [])));
        public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
            new(new DeleteOutcome.Rejected(ArmError.Validation("stub", [])));
        public ValueTask<PagedResult<ResourceEnvelope<LockProperties>>> ListAsync(ResourceListRequest req, CancellationToken ct) =>
            new(new PagedResult<ResourceEnvelope<LockProperties>>([], null));
        public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
            new(new ActionOutcome.Rejected(ArmError.Validation("stub", [])));
    }
}
