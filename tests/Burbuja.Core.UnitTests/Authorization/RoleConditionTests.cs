using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Sdk.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Authorization;

/// <summary>
/// C5 (N7): ARM-shaped role-assignment conditions. A condition is evaluated against the request
/// context; a malformed one denies, and a conditional assignment is never memoised.
/// </summary>
public sealed class RoleConditionTests
{
    [Fact]
    public void Parser_handles_every_supported_clause()
    {
        var context = new RoleConditionContext(
            "widget-1",
            new Dictionary<string, string> { ["env"] = "prod" },
            new Dictionary<string, string> { ["tier"] = "gold" },
            "p1",
            new Dictionary<string, string> { ["dept"] = "eng" });

        RoleConditionParser.TryEvaluate(
            "AllOf(@Resource[name] eq 'widget-1', @Resource[tags:env] eq 'prod', @Request[tags:tier] startswith 'go', @Principal[id] eq 'p1', @Principal[claims:dept] eq 'eng')",
            context,
            out var allowed).ShouldBeTrue();
        allowed.ShouldBeTrue();

        RoleConditionParser.TryEvaluate("AllOf(@Resource[tags:env] ne 'dev')", context, out allowed).ShouldBeTrue();
        allowed.ShouldBeTrue();

        RoleConditionParser.TryEvaluate("AllOf(@Resource[tags:env] eq 'dev')", context, out allowed).ShouldBeTrue();
        allowed.ShouldBeFalse();
    }

    [Fact]
    public void Malformed_condition_is_rejected_and_denies()
    {
        RoleConditionParser.TryParse("AllOf(@Resource[name] like 'x')", out var parsed, out var error).ShouldBeFalse();
        parsed.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();

        var allAllowed = RoleConditionParser.TryEvaluate("not-a-condition", new RoleConditionContext(null, null, null, null, null), out var allowed);
        allAllowed.ShouldBeFalse(); // could not evaluate
        allowed.ShouldBeFalse();

        // A missing attribute denies even for a well-formed clause.
        RoleConditionParser.TryEvaluate("AllOf(@Resource[tags:env] eq 'prod')", new RoleConditionContext(null, null, null, null, null), out allowed)
            .ShouldBeTrue();
        allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Conditional_assignment_allows_when_the_condition_is_true()
    {
        using var sp = CreateHost();
        var evaluator = AddConditionalAssignment(sp, "AllOf(@Resource[name] eq 'allowed')");

        var result = await evaluator.AuthorizeAsync(
            Principal("cond"),
            "/subscriptions/sub1/resourceGroups/rg1",
            "Burbuja.Agents/widgets/read",
            isDataAction: false,
            new AuthorizationContext(ResourceName: "allowed"),
            TestContext.Current.CancellationToken);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Conditional_assignment_denies_when_the_condition_is_false()
    {
        using var sp = CreateHost();
        var evaluator = AddConditionalAssignment(sp, "AllOf(@Resource[name] eq 'allowed')");

        var result = await evaluator.AuthorizeAsync(
            Principal("cond"),
            "/subscriptions/sub1/resourceGroups/rg1",
            "Burbuja.Agents/widgets/read",
            isDataAction: false,
            new AuthorizationContext(ResourceName: "blocked"),
            TestContext.Current.CancellationToken);

        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Conditional_assignments_are_not_memoised()
    {
        using var sp = CreateHost();
        var evaluator = AddConditionalAssignment(sp, "AllOf(@Resource[name] eq 'allowed')");
        var principal = Principal("cond");
        const string scope = "/subscriptions/sub1/resourceGroups/rg1";
        const string action = "Burbuja.Agents/widgets/read";

        var first = await evaluator.AuthorizeAsync(
            principal, scope, action, false, new AuthorizationContext(ResourceName: "allowed"), TestContext.Current.CancellationToken);
        var second = await evaluator.AuthorizeAsync(
            principal, scope, action, false, new AuthorizationContext(ResourceName: "blocked"), TestContext.Current.CancellationToken);

        first.Allowed.ShouldBeTrue();
        second.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Condition_and_version_survive_the_handler_and_rehydration()
    {
        using var host1 = CreateHost();
        (await Send(host1, "PUT", "/subscriptions/sub1", """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var put = await Send(host1, "PUT",
            "/subscriptions/sub1/providers/Burbuja.Authorization/roleAssignments/a1",
            """
            {"properties":{"roleDefinitionId":"Reader","principalId":"cond","condition":"AllOf(@Resource[name] eq 'allowed')","conditionVersion":"1.0"}}
            """);
        put.StatusCode.ShouldBeOneOf(200, 201);

        var evaluator1 = host1.GetRequiredService<AuthorizationEvaluator>();
        (await evaluator1.AuthorizeAsync(
            Principal("cond"), "/subscriptions/sub1", "Burbuja.Agents/widgets/read", false,
            new AuthorizationContext(ResourceName: "allowed"), TestContext.Current.CancellationToken)).Allowed.ShouldBeTrue();
        (await evaluator1.AuthorizeAsync(
            Principal("cond"), "/subscriptions/sub1", "Burbuja.Agents/widgets/read", false,
            new AuthorizationContext(ResourceName: "blocked"), TestContext.Current.CancellationToken)).Allowed.ShouldBeFalse();

        // A new container over the same store rehydrates the conditional assignment.
        var store = host1.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>();
        using var host2 = CreateHost(store);
        var evaluator2 = host2.GetRequiredService<AuthorizationEvaluator>();
        (await evaluator2.AuthorizeAsync(
            Principal("cond"), "/subscriptions/sub1", "Burbuja.Agents/widgets/read", false,
            new AuthorizationContext(ResourceName: "allowed"), TestContext.Current.CancellationToken)).Allowed.ShouldBeTrue();
        (await evaluator2.AuthorizeAsync(
            Principal("cond"), "/subscriptions/sub1", "Burbuja.Agents/widgets/read", false,
            new AuthorizationContext(ResourceName: "blocked"), TestContext.Current.CancellationToken)).Allowed.ShouldBeFalse();
    }

    private static ArmPrincipal Principal(string id) =>
        new(id, "User", null, [], new Dictionary<string, string> { ["dept"] = "eng" });

    private static AuthorizationEvaluator AddConditionalAssignment(IServiceProvider sp, string condition)
    {
        var evaluator = sp.GetRequiredService<AuthorizationEvaluator>();
        evaluator.UpsertAssignment(new RoleAssignment(
            "cond-assignment",
            "/",
            "cond",
            "Reader",
            DateTimeOffset.UnixEpoch,
            "test",
            condition,
            "1.0"));
        return evaluator;
    }

    private static async Task<ArmRequestContext> Send(IServiceProvider sp, string method, string path, string? body = null)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["api-version"] = ArmConstants.ApiVersion };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = "owner-key" };
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

    private static ServiceProvider CreateHost(Burbuja.Core.Abstractions.Arm.IResourceStore? sharedStore = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-cond-plugins-" + Guid.NewGuid().ToString("N"));
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
        if (sharedStore is not null)
            services.AddSingleton(sharedStore);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
