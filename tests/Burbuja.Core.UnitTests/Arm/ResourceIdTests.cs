using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

public sealed class ResourceIdTests
{
    private readonly IArmTypeResolver _resolver = new FakeResolver();

    [Fact]
    public void Subscription()
    {
        Parse("/subscriptions/sub1").SubscriptionId.ShouldBe("sub1");
        Parse("/subscriptions/sub1").ResourceGroup.ShouldBeNull();
        Parse("/subscriptions/sub1").Parent.ShouldBeNull();
        Parse("/subscriptions/sub1").IsCollection.ShouldBeTrue();
    }

    [Fact]
    public void Resource_group()
    {
        var id = Parse("/subscriptions/sub1/resourceGroups/rg1");
        id.ResourceGroup.ShouldBe("rg1");
        id.Parent!.SubscriptionId.ShouldBe("sub1");
        id.Parent.ResourceGroup.ShouldBeNull();
        id.IsCollection.ShouldBeFalse();
        id.Canonical.ShouldBe("/subscriptions/sub1/resourceGroups/rg1");
    }

    [Fact]
    public void Missing_leading_slash_fails()
    {
        ResourceId.TryParse("subscriptions/s", out _, out var error).ShouldBeFalse();
        error!.Code.ShouldBe("InvalidResourceUri");
    }

    [Fact]
    public void Empty_path_fails()
    {
        ResourceId.TryParse("/", out _, out var error).ShouldBeFalse();
        error!.Code.ShouldBe("InvalidResourceUri");
    }

    [Fact]
    public void Missing_subscription_id_fails()
    {
        ResourceId.TryParse("/subscriptions", out _, out var error).ShouldBeFalse();
        error!.Code.ShouldBe("InvalidResourceUri");
    }

    [Fact]
    public void Simple_tracked_resource()
    {
        var id = Parse("/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/workspaces/ws1");
        id.Namespace.ShouldBe("Burbuja.Agents");
        id.FullyQualifiedType.ShouldBe("workspaces");
        id.NameSegments.ShouldBe(["ws1"]);
        id.IsCollection.ShouldBeFalse();
        id.Parent!.Canonical.ShouldBe("/subscriptions/sub1/resourceGroups/rg1");
    }

    [Fact]
    public void Child_resource()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1");
        id.FullyQualifiedType.ShouldBe("workspaces/jobs");
        id.NameSegments.ShouldBe(["ws1", "j1"]);
        id.IsCollection.ShouldBeFalse();
        id.Parent!.FullyQualifiedType.ShouldBe("workspaces");
        id.Parent.NameSegments.ShouldBe(["ws1"]);
        id.Canonical.ShouldBe("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1");
    }

    [Fact]
    public void Resource_groups_collection()
    {
        var id = Parse("/subscriptions/s/resourceGroups");
        id.IsCollection.ShouldBeTrue();
        id.ResourceGroup.ShouldBeNull();
        id.FullyQualifiedType.ShouldBe("resourceGroups");
        id.Parent!.SubscriptionId.ShouldBe("s");
    }

    [Fact]
    public void Child_collection_is_not_an_action()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs");
        id.IsCollection.ShouldBeTrue();
        id.FullyQualifiedType.ShouldBe("workspaces/jobs");
        id.Action.ShouldBeNull();
        id.NameSegments.ShouldBe(["ws1"]);
        id.Parent!.NameSegments.ShouldBe(["ws1"]);
    }

    [Fact]
    public void Without_resolver_child_collection_still_parses_as_collection()
    {
        ResourceId.TryParse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs", out var id, out _).ShouldBeTrue();
        id.IsCollection.ShouldBeTrue();
        id.Action.ShouldBeNull();
        id.FullyQualifiedType.ShouldBe("workspaces/jobs");
    }

    [Fact]
    public void Action_on_a_named_resource()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/cancel");
        id.Action.ShouldBe("cancel");
        id.DataPlaneSegment.ShouldBeNull();
        id.FullyQualifiedType.ShouldBe("workspaces/jobs");
        id.IsCollection.ShouldBeFalse();
    }

    [Fact]
    public void Data_plane_segment()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/events");
        id.DataPlaneSegment.ShouldBe("events");
        id.Action.ShouldBeNull();
        id.IsCollection.ShouldBeFalse();
    }

    [Fact]
    public void Resolver_distinguishes_action_from_data_plane()
    {
        var events = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/events");
        events.DataPlaneSegment.ShouldBe("events");
        var cancel = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/cancel");
        cancel.Action.ShouldBe("cancel");
    }

    [Fact]
    public void Provider_level_register_action()
    {
        var id = Parse("/subscriptions/s/providers/Burbuja.Agents/register");
        id.Namespace.ShouldBe("Burbuja.Agents");
        id.Action.ShouldBe("register");
        id.TypeSegments.Length.ShouldBe(0);
    }

    [Fact]
    public void Subscription_less_collection()
    {
        var id = Parse("/providers/Burbuja.Deployments/targets");
        id.SubscriptionId.ShouldBe("");
        id.Namespace.ShouldBe("Burbuja.Deployments");
        id.FullyQualifiedType.ShouldBe("targets");
        id.IsCollection.ShouldBeTrue();
        id.Canonical.ShouldBe("/providers/Burbuja.Deployments/targets");
    }

    [Fact]
    public void Subscription_less_named()
    {
        var id = Parse("/providers/Burbuja.Deployments/targets/in-memory");
        id.IsCollection.ShouldBeFalse();
        id.NameSegments.ShouldBe(["in-memory"]);
        id.Canonical.ShouldBe("/providers/Burbuja.Deployments/targets/in-memory");
    }

    [Fact]
    public void Extension_resource_parent_is_the_target()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/providers/Burbuja.Authorization/roleAssignments/ra1");
        id.Namespace.ShouldBe("Burbuja.Authorization");
        id.FullyQualifiedType.ShouldBe("roleAssignments");
        id.NameSegments.ShouldBe(["ra1"]);
        id.Parent!.Namespace.ShouldBe("Burbuja.Agents");
        id.Parent.FullyQualifiedType.ShouldBe("workspaces");
        id.Canonical.ShouldBe("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/providers/Burbuja.Authorization/roleAssignments/ra1");
    }

    [Fact]
    public void Nested_three_level_type()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/runs/r1");
        id.FullyQualifiedType.ShouldBe("workspaces/jobs/runs");
        id.NameSegments.ShouldBe(["ws1", "j1", "r1"]);
        id.Parent!.FullyQualifiedType.ShouldBe("workspaces/jobs");
        id.Parent.Parent!.FullyQualifiedType.ShouldBe("workspaces");
    }

    [Fact]
    public void Locations_operations_path()
    {
        var id = Parse("/subscriptions/s/providers/Burbuja.Agents/locations/local/operations/op1");
        id.FullyQualifiedType.ShouldBe("locations/operations");
        id.NameSegments.ShouldBe(["local", "op1"]);
    }

    [Fact]
    public void Locations_operationResults_path()
    {
        var id = Parse("/subscriptions/s/providers/Burbuja.Agents/locations/local/operationResults/op1");
        id.FullyQualifiedType.ShouldBe("locations/operationResults");
        id.NameSegments.ShouldBe(["local", "op1"]);
    }

    [Fact]
    public void Key_is_lower_invariant_canonical_preserves_casing()
    {
        var id = Parse("/subscriptions/Sub1/resourceGroups/RgOne/providers/Burbuja.Agents/workspaces/MyWs");
        id.Canonical.ShouldContain("Sub1");
        id.Canonical.ShouldContain("RgOne");
        id.Canonical.ShouldContain("MyWs");
        id.Key.ShouldBe(id.Canonical.ToLowerInvariant());
        id.Key.Equals(id.Canonical, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void Collection_flag_on_list_path()
    {
        Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces").IsCollection.ShouldBeTrue();
        Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1").IsCollection.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_trailing_segment_with_resolver_is_invalid()
    {
        ResourceId.TryParse(
            "/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/notAThing",
            _resolver,
            out _,
            out var error).ShouldBeFalse();
        error!.Code.ShouldBe("InvalidResourceType");
    }

    [Fact]
    public void Expected_providers_missing()
    {
        ResourceId.TryParse("/subscriptions/s/notProviders", out _, out var error).ShouldBeFalse();
        error!.Code.ShouldBe("InvalidResourceUri");
    }

    [Fact]
    public void Parent_of_workspace_is_resource_group()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws");
        id.Parent!.ResourceGroup.ShouldBe("rg");
        id.Parent.Namespace.ShouldBe("");
    }

    [Fact]
    public void Parent_of_resource_group_is_subscription()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg");
        id.Parent!.SubscriptionId.ShouldBe("s");
        id.Parent.Parent.ShouldBeNull();
    }

    [Fact]
    public void Data_plane_on_child_collection_parent()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/artifacts");
        id.DataPlaneSegment.ShouldBe("artifacts");
        id.FullyQualifiedType.ShouldBe("workspaces/jobs");
    }

    [Fact]
    public void Extension_on_subscription()
    {
        var id = Parse("/subscriptions/s/providers/Burbuja.Authorization/roleAssignments/ra1");
        id.Parent!.SubscriptionId.ShouldBe("s");
        id.Parent.Namespace.ShouldBe("");
        id.Canonical.ShouldBe("/subscriptions/s/providers/Burbuja.Authorization/roleAssignments/ra1");
    }

    [Fact]
    public void TryParse_without_resolver_overload_still_works()
    {
        ResourceId.TryParse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Authorization/locks/lock1", out var id, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        id.FullyQualifiedType.ShouldBe("locks");
        id.NameSegments.ShouldBe(["lock1"]);
    }

    [Fact]
    public async Task Collection_level_POST_returns_405()
    {
        using var sp = CreateHost();
        var ctx = new ArmRequestContext
        {
            Method = "POST",
            Path = "/subscriptions/sub1/resourceGroups",
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string> { ["x-api-key"] = "owner-key" },
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        ctx.StatusCode.ShouldBe(405);
        ctx.Error!.Code.ShouldBe("MethodNotAllowed");
    }

    [Fact]
    public void Provider_get_empty_types()
    {
        var id = Parse("/subscriptions/s/providers/Burbuja.Agents");
        id.Namespace.ShouldBe("Burbuja.Agents");
        id.TypeSegments.Length.ShouldBe(0);
        id.Action.ShouldBeNull();
    }

    [Fact]
    public void Action_on_workspace()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/explainPolicy");
        id.Action.ShouldBe("explainPolicy");
        id.FullyQualifiedType.ShouldBe("workspaces");
    }

    [Fact]
    public void Canonical_of_child_preserves_parent_path()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1");
        id.Canonical.ShouldBe("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1");
        id.Parent!.Canonical.ShouldBe("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1");
    }

    [Fact]
    public void Runs_collection_under_job()
    {
        var id = Parse("/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/ws1/jobs/j1/runs");
        id.IsCollection.ShouldBeTrue();
        id.FullyQualifiedType.ShouldBe("workspaces/jobs/runs");
        id.Action.ShouldBeNull();
    }

    private ResourceId Parse(string path)
    {
        ResourceId.TryParse(path, _resolver, out var id, out var error).ShouldBeTrue($"parse failed: {error?.Code} {error?.Message}");
        return id;
    }

    private static ServiceProvider CreateHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:NodeId"] = "n",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private sealed class FakeResolver : IArmTypeResolver
    {
        private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            "workspaces", "workspaces/jobs", "workspaces/jobs/runs",
            "targets", "roleAssignments", "locks", "locations", "locations/operations", "locations/operationResults",
        };

        private static readonly HashSet<string> Actions = new(StringComparer.OrdinalIgnoreCase)
        {
            "workspaces|explainPolicy",
            "workspaces/jobs|cancel",
            "workspaces/jobs|accept",
            "|register",
        };

        private static readonly HashSet<string> DataPlane = new(StringComparer.OrdinalIgnoreCase)
        {
            "workspaces/jobs|events",
            "workspaces/jobs|artifacts",
        };

        public bool IsType(string ns, string fqType) => Types.Contains(fqType);

        public bool IsAction(string ns, string fqType, string name) =>
            Actions.Contains($"{fqType}|{name}")
            || (fqType.Length == 0 && name.Equals("register", StringComparison.OrdinalIgnoreCase));

        public bool IsDataPlaneSegment(string ns, string fqType, string name) =>
            DataPlane.Contains($"{fqType}|{name}");
    }
}
