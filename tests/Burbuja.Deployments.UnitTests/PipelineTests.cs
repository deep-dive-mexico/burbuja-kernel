using Burbuja.Core.Abstractions.Events;
using Burbuja.Deployments.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class PipelineTests
{
    [Fact]
    public async Task Put_returns_202_headers()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        put.StatusCode.ShouldBe(202);
        put.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");
        put.ResponseHeaders["Azure-AsyncOperation"].ShouldContain("/operations/");
        put.ResponseHeaders.ShouldContainKey("Location");
        put.ResponseHeaders.ShouldContainKey("Retry-After");
        put.ResponseHeaders.ShouldContainKey("x-ms-request-id");
    }

    [Fact]
    public async Task Poll_operation_to_succeeded()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        put.Ok();
        await Harness.RunDeploymentAsync(sp, Harness.DepPath, TestContext.Current.CancellationToken);
        var opUrl = put.ResponseHeaders["Azure-AsyncOperation"];
        var q = opUrl.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? opUrl : opUrl[..q];
        var poll = await Harness.Send(sp, "GET", path);
        poll.Ok();
        poll.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Get_shows_steps()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"))).Ok();
        await Harness.RunDeploymentAsync(sp, Harness.DepPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.DepPath);
        get.Ok();
        var steps = get.AsJson()["properties"]!["steps"]!.AsArray();
        steps.Count.ShouldBe(1);
        steps[0]!["resource"]!.GetValue<string>().ShouldBe("web");
        steps[0]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        get.AsJson()["properties"]!["executionState"]!.GetValue<string>().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Child_collection_has_value_and_nextLink()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"))).Ok();
        var list = await Harness.Send(sp, "GET", Harness.EnvPath + "/deployments");
        list.Ok();
        var obj = list.AsJson();
        obj.ContainsKey("value").ShouldBeTrue();
        obj.ContainsKey("nextLink").ShouldBeTrue();
        obj["value"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reader_cannot_put()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"), key: "reader-key");
        put.StatusCode.ShouldBe(403);
        put.Error!.Code.ShouldBe("AuthorizationFailed");
    }

    [Fact]
    public async Task Invalid_template_is_400_with_pointers()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, """{"properties":{"template":{"schema":"burbuja.deployments/v1","resources":[{"name":"web","kind":"process","dependsOn":["missing"]}]}}}""");
        put.StatusCode.ShouldBe(400);
        put.Error.ShouldNotBeNull();
        put.Error.Details.ShouldContain(d => d.Target != null && d.Target.Contains("/dependsOn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sse_tail_is_ordered_and_resumes()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("alpha", "bravo"))).Ok();
        await Harness.RunDeploymentAsync(sp, Harness.DepPath, TestContext.Current.CancellationToken);
        var log = sp.GetRequiredService<IEventLog>();
        var streamId = DeploymentsConstants.StreamId(Harness.DepPath);
        var all = new List<LedgerEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var ev in log.TailAsync(streamId, 0, cts.Token))
            {
                all.Add(ev);
                if (ev.Type == DeploymentEvents.Succeeded)
                    break;
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
        }

        all.ShouldContain(e => e.Type == DeploymentEvents.Accepted);
        all.ShouldContain(e => e.Type == DeploymentEvents.Started);
        all.ShouldContain(e => e.Type == DeploymentEvents.Succeeded);
        var first = all[0].Seq;
        var resumed = new List<LedgerEvent>();
        using var resumeCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        resumeCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var ev in log.TailAsync(streamId, first, resumeCts.Token))
            {
                resumed.Add(ev);
                if (ev.Type == DeploymentEvents.Succeeded)
                    break;
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
        }

        resumed.ShouldNotContain(e => e.Seq == first);
        resumed.Select(e => e.Seq).ShouldBe(all.Skip(1).Select(e => e.Seq));
    }
}
