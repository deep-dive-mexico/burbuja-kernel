using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Arm;
using Burbuja.Core.Arm.Rps;
using Burbuja.Core.Clock;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Plugin;
using Burbuja.Core.Sdk.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

public sealed class RbacTests
{
    [Fact]
    public async Task Assignment_at_resource_group_does_not_authorize_subscription()
    {
        var evaluator = IsolatedEvaluator(
            AuthorizationEvaluator.BuiltIns(),
            [new RoleAssignment("a1", "/subscriptions/sub1/resourceGroups/rg1", "alice", "Owner", DateTimeOffset.UnixEpoch, "system", null)]);
        var principal = Principal("alice");
        var rg = await evaluator.AuthorizeAsync(principal, "/subscriptions/sub1/resourceGroups/rg1", "Burbuja.Resources/resourceGroups/write", false, CancellationToken.None);
        rg.Allowed.ShouldBeTrue();
        var sub = await evaluator.AuthorizeAsync(principal, "/subscriptions/sub1", "Burbuja.Resources/subscriptions/write", false, CancellationToken.None);
        sub.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Assignment_at_subscription_authorizes_resource_group()
    {
        var evaluator = IsolatedEvaluator(
            AuthorizationEvaluator.BuiltIns(),
            [new RoleAssignment("a1", "/subscriptions/sub1", "alice", "Owner", DateTimeOffset.UnixEpoch, "system", null)]);
        var result = await evaluator.AuthorizeAsync(Principal("alice"), "/subscriptions/sub1/resourceGroups/rg1", "Burbuja.Resources/resourceGroups/write", false, CancellationToken.None);
        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task NotActions_are_exclusions_not_deny_overrides()
    {
        var defs = AuthorizationEvaluator.BuiltIns().Concat(
        [
            new RoleDefinition("AuthWriter", "AuthWriter", ["Burbuja.Authorization/roleAssignments/write"], [], [], [], ["/"], false),
        ]).ToArray();
        var evaluator = IsolatedEvaluator(defs,
        [
            new RoleAssignment("c", "/", "alice", "Contributor", DateTimeOffset.UnixEpoch, "system", null),
            new RoleAssignment("w", "/", "alice", "AuthWriter", DateTimeOffset.UnixEpoch, "system", null),
        ]);
        (await evaluator.AuthorizeAsync(Principal("alice"), "/", "Burbuja.Authorization/roleAssignments/write", false, CancellationToken.None)).Allowed.ShouldBeTrue();
        var onlyContributor = IsolatedEvaluator(AuthorizationEvaluator.BuiltIns(),
            [new RoleAssignment("c", "/", "bob", "Contributor", DateTimeOffset.UnixEpoch, "system", null)]);
        (await onlyContributor.AuthorizeAsync(Principal("bob"), "/", "Burbuja.Authorization/roleAssignments/write", false, CancellationToken.None)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Reader_star_read_matches_catalogue_reads()
    {
        var evaluator = IsolatedEvaluator(
            AuthorizationEvaluator.BuiltIns(),
            [new RoleAssignment("r", "/", "reader", "Reader", DateTimeOffset.UnixEpoch, "system", null)]);
        (await evaluator.AuthorizeAsync(Principal("reader"), "/subscriptions/sub1", "Burbuja.Resources/subscriptions/read", false, CancellationToken.None)).Allowed.ShouldBeTrue();
        (await evaluator.AuthorizeAsync(Principal("reader"), "/subscriptions/sub1", "Burbuja.Resources/subscriptions/write", false, CancellationToken.None)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Decision_cache_invalidates_on_role_write()
    {
        var evaluator = IsolatedEvaluator(AuthorizationEvaluator.BuiltIns(), []);
        var principal = Principal("alice");
        (await evaluator.AuthorizeAsync(principal, "/", "Burbuja.Resources/subscriptions/write", false, CancellationToken.None)).Allowed.ShouldBeFalse();
        evaluator.UpsertAssignment(new RoleAssignment("a", "/", "alice", "Owner", DateTimeOffset.UnixEpoch, "system", null));
        (await evaluator.AuthorizeAsync(principal, "/", "Burbuja.Resources/subscriptions/write", false, CancellationToken.None)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Rehydrate_loads_custom_definitions_and_assignments_from_store()
    {
        using var sp = CreateHost();
        var store = sp.GetRequiredService<IResourceStore>();
        var clock = sp.GetRequiredService<IClock>();
        var def = new ResourceEnvelope<JsonObject>(
            "/subscriptions/sub1/providers/Burbuja.Authorization/roleDefinitions/custom",
            "custom",
            "Burbuja.Authorization/roleDefinitions",
            "local",
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToNode(new RoleDefinitionProperties
            {
                RoleName = "custom",
                Actions = ["Burbuja.Resources/subscriptions/read"],
                AssignableScopes = ["/"],
            })!.AsObject(),
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "User", clock.UtcNow, "system", "User"),
            "");
        await store.UpsertAsync(def, null, CancellationToken.None);
        var assignment = new ResourceEnvelope<JsonObject>(
            "/subscriptions/sub1/providers/Burbuja.Authorization/roleAssignments/as1",
            "as1",
            "Burbuja.Authorization/roleAssignments",
            "local",
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToNode(new RoleAssignmentProperties
            {
                RoleDefinitionId = "custom",
                PrincipalId = "carol",
            })!.AsObject(),
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "User", clock.UtcNow, "system", "User"),
            "");
        await store.UpsertAsync(assignment, null, CancellationToken.None);

        var evaluator = sp.GetRequiredService<AuthorizationEvaluator>();
        evaluator.Rehydrate(store);
        (await evaluator.AuthorizeAsync(Principal("carol"), "/subscriptions/sub1", "Burbuja.Resources/subscriptions/read", false, CancellationToken.None)).Allowed.ShouldBeTrue();
        (await evaluator.AuthorizeAsync(Principal("carol"), "/subscriptions/sub1", "Burbuja.Resources/subscriptions/write", false, CancellationToken.None)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void IRoleContributor_unknown_action_fails_boot_validation()
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
        services.AddSingleton<IRoleContributor>(new EvilContributor());
        services.AddBurbujaCore(config);
        using var sp = services.BuildServiceProvider();
        Should.Throw<PluginBootException>(() => sp.GetRequiredService<ArmRoutingTable>());
    }

    [Fact]
    public void IRoleContributor_wildcard_actions_are_accepted()
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
        services.AddSingleton<IRoleContributor>(new WildcardContributor());
        services.AddBurbujaCore(config);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ArmRoutingTable>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Role_assignment_scope_is_the_extension_parent()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var put = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/roleAssignments/ra1",
            body: """{"properties":{"roleDefinitionId":"Reader","principalId":"dave"}}""");
        put.StatusCode.ShouldBeOneOf(200, 201);
        var evaluator = sp.GetRequiredService<AuthorizationEvaluator>();
        (await evaluator.AuthorizeAsync(Principal("dave"), "/subscriptions/sub1/resourceGroups/rg1", "Burbuja.Resources/resourceGroups/read", false, CancellationToken.None)).Allowed.ShouldBeTrue();
        (await evaluator.AuthorizeAsync(Principal("dave"), "/subscriptions/sub1", "Burbuja.Resources/subscriptions/read", false, CancellationToken.None)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AssignableScopes_are_enforced()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var def = await Send(sp, "PUT", "/subscriptions/sub1/providers/Burbuja.Authorization/roleDefinitions/narrow",
            body: """{"properties":{"roleName":"narrow","actions":["Burbuja.Resources/resourceGroups/read"],"assignableScopes":["/subscriptions/sub1/resourceGroups/rg1"]}}""");
        def.StatusCode.ShouldBeOneOf(200, 201);
        var atSub = await Send(sp, "PUT", "/subscriptions/sub1/providers/Burbuja.Authorization/roleAssignments/blocked",
            body: """{"properties":{"roleDefinitionId":"narrow","principalId":"erin"}}""");
        atSub.StatusCode.ShouldBe(403);
    }

    [Fact]
    public void ArmActionMatcher_path_boundary_does_not_prefix_sibling_ids()
    {
        ArmActionMatcher.AssignmentCoversRequest("/subscriptions/sub", "/subscriptions/sub1").ShouldBeFalse();
        ArmActionMatcher.AssignmentCoversRequest("/subscriptions/sub1", "/subscriptions/sub1/resourceGroups/rg").ShouldBeTrue();
    }

    private static AuthorizationEvaluator IsolatedEvaluator(IReadOnlyList<RoleDefinition> defs, IReadOnlyList<RoleAssignment> assignments)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, TimeProviderClock>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IResourceStore>(new InMemoryResourceStore(new TimeProviderClock(TimeProvider.System)));
        services.Configure<Burbuja.Core.Options.AuthOptions>(_ => { });
        using var sp = services.BuildServiceProvider();
        var built = AuthorizationEvaluator.Create(sp);
        foreach (var d in defs)
            built.UpsertDefinition(d);
        foreach (var a in assignments)
            built.UpsertAssignment(a);
        return built;
    }

    private static ArmPrincipal Principal(string id) => new(id, "User", null, [], new Dictionary<string, string>());

    private static ServiceProvider CreateHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-empty-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Kernel:NodeId"] = "n",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task SeedSubscription(ServiceProvider sp)
    {
        var ctx = await Send(sp, "PUT", "/subscriptions/sub1", body: """{"properties":{"displayName":"s","state":"Enabled"}}""");
        ctx.StatusCode.ShouldBeOneOf(200, 201);
    }

    private static async Task<ArmRequestContext> Send(IServiceProvider sp, string method, string path, string? body = null)
    {
        var ctx = new ArmRequestContext
        {
            Method = method,
            Path = path,
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string> { ["x-api-key"] = "owner-key" },
            Body = body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body)),
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private sealed class EvilContributor : IRoleContributor
    {
        public IReadOnlyList<RoleDefinition> Roles { get; } =
        [
            new("evil", "evil", ["NotAReal.Provider/widgets/write"], [], [], [], ["/"], false),
        ];
    }

    private sealed class WildcardContributor : IRoleContributor
    {
        public IReadOnlyList<RoleDefinition> Roles { get; } =
        [
            new("wild", "wild", ["*/read", "Burbuja.Resources/*"], [], [], [], ["/"], false),
        ];
    }
}
