using System.Text.Json.Nodes;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Execution;
using Burbuja.Deployments.Targets;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class PlannerTests
{
    private static readonly HashSet<DeploymentResourceKind> All = [.. Enum.GetValues<DeploymentResourceKind>()];

    [Fact]
    public void Duplicate_name_is_invalid()
    {
        var t = T(R("web"), R("web"));
        var plan = DeploymentPlanner.Validate(t, All, DeploymentMode.Incremental);
        plan.Errors.ShouldContain(e => e.Message.Contains("Duplicate", StringComparison.Ordinal));
        var target = plan.Errors[0].Target;
        target.ShouldNotBeNull();
        target.ShouldContain("/name");
    }

    [Fact]
    public void Unknown_dependsOn_is_invalid()
    {
        var t = T(R("web", depends: ["db"]));
        var plan = DeploymentPlanner.Validate(t, All, DeploymentMode.Incremental);
        plan.Errors.ShouldContain(e => e.Message.Contains("Unknown dependsOn", StringComparison.Ordinal));
    }

    [Fact]
    public void Cycle_is_named()
    {
        var t = T(R("alpha", depends: ["bravo"]), R("bravo", depends: ["alpha"]));
        var plan = DeploymentPlanner.Validate(t, All, DeploymentMode.Incremental);
        plan.Errors.ShouldContain(e => e.Message.Contains("Cycle", StringComparison.Ordinal) && e.Message.Contains("alpha", StringComparison.Ordinal) && e.Message.Contains("bravo", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_kind_is_invalid()
    {
        var t = T(R("web", kind: DeploymentResourceKind.Database));
        var plan = DeploymentPlanner.Validate(t, [DeploymentResourceKind.Process], DeploymentMode.Incremental);
        plan.Errors.ShouldContain(e => e.Message.Contains("not supported", StringComparison.Ordinal));
        var kindTarget = plan.Errors[0].Target;
        kindTarget.ShouldNotBeNull();
        kindTarget.ShouldContain("/kind");
    }

    [Fact]
    public void Waves_are_stable_for_chain()
    {
        var waves = DeploymentPlanner.Waves([R("a"), R("b", depends: ["a"]), R("c", depends: ["b"])]);
        waves.Select(w => w.ToArray()).ShouldBe([new[] { "a" }, new[] { "b" }, new[] { "c" }]);
    }

    [Fact]
    public void Waves_are_stable_for_diamond()
    {
        var waves = DeploymentPlanner.Waves([R("a"), R("b", depends: ["a"]), R("c", depends: ["a"]), R("d", depends: ["b", "c"])]);
        waves[0].ShouldBe(["a"]);
        waves[1].ShouldBe(["b", "c"]);
        waves[2].ShouldBe(["d"]);
    }

    [Fact]
    public void Waves_are_stable_for_fan_out()
    {
        var waves = DeploymentPlanner.Waves([R("a"), R("b", depends: ["a"]), R("c", depends: ["a"])]);
        waves[0].ShouldBe(["a"]);
        waves[1].ShouldBe(["b", "c"]);
    }

    [Fact]
    public void Waves_are_stable_for_disconnected()
    {
        var waves = DeploymentPlanner.Waves([R("z"), R("a"), R("m")]);
        waves.ShouldHaveSingleItem();
        waves[0].ShouldBe(["a", "m", "z"]);
    }

    [Fact]
    public void Implicit_reference_adds_edge()
    {
        var child = R("web");
        child.Spec["url"] = "${steps.db.outputs.host}";
        var t = T(R("db"), child);
        var plan = DeploymentPlanner.Validate(t, All, DeploymentMode.Incremental);
        plan.Errors.ShouldBeEmpty();
        plan.Waves.Count.ShouldBe(2);
        plan.Waves[0].ShouldBe(["db"]);
        plan.Waves[1].ShouldBe(["web"]);
    }

    [Fact]
    public void Reference_cycle_is_named()
    {
        var a = R("alpha");
        a.Spec["x"] = "${steps.bravo.outputs.y}";
        var b = R("bravo");
        b.Spec["y"] = "${steps.alpha.outputs.x}";
        var plan = DeploymentPlanner.Validate(T(a, b), All, DeploymentMode.Incremental);
        plan.Errors.ShouldContain(e => e.Message.Contains("Cycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_deletes_orphans()
    {
        var target = new InMemoryTarget(TimeProvider.System);
        await target.ApplyAsync(Req("old"), TestContext.Current.CancellationToken);
        var plan = await target.PlanAsync(new TargetPlanRequest("env", DeploymentMode.Complete, [R("new")]), TestContext.Current.CancellationToken);
        plan.Changes.ShouldContain(c => c.Resource == "old" && c.Change == StepChange.Delete);
        plan.Changes.ShouldContain(c => c.Resource == "new" && c.Change == StepChange.Create);
    }

    [Fact]
    public async Task Incremental_does_not_delete_orphans()
    {
        var target = new InMemoryTarget(TimeProvider.System);
        await target.ApplyAsync(Req("old"), TestContext.Current.CancellationToken);
        var plan = await target.PlanAsync(new TargetPlanRequest("env", DeploymentMode.Incremental, [R("new")]), TestContext.Current.CancellationToken);
        plan.Changes.ShouldNotContain(c => c.Change == StepChange.Delete);
    }

    [Fact]
    public async Task Unchanged_spec_is_noop()
    {
        var target = new InMemoryTarget(TimeProvider.System);
        var spec = new JsonObject { ["k"] = 1 };
        var r = R("keep");
        r.Spec = spec;
        await target.ApplyAsync(Req("keep", spec), TestContext.Current.CancellationToken);
        var plan = await target.PlanAsync(new TargetPlanRequest("env", DeploymentMode.Incremental, [r]), TestContext.Current.CancellationToken);
        plan.Changes.ShouldContain(c => c.Resource == "keep" && c.Change == StepChange.NoOp);
    }

    private static DeploymentTemplate T(params DeploymentResource[] resources) => new() { Resources = [.. resources] };

    private static DeploymentResource R(string name, DeploymentResourceKind kind = DeploymentResourceKind.Process, string[]? depends = null) =>
        new() { Name = name, Kind = kind, DependsOn = depends?.ToList() ?? [] };

    private static StepRequest Req(string name, JsonObject? spec = null) =>
        new("env", "dep", name, DeploymentResourceKind.Process, StepChange.Create, spec ?? [], [], [], 1, TimeSpan.FromSeconds(5));
}
