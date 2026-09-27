using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

/// <summary>
/// C6 (N9): the in-process <c>IArmClient</c> runs the real middleware chain, so RBAC, ETags and
/// error envelopes behave exactly as they do over HTTP — there is no system-principal bypass.
/// </summary>
public sealed class ArmPipelineClientTests
{
    [Fact]
    public async Task In_proc_client_gets_a_subscription_with_a_key()
    {
        using var sp = CreateHost();
        var client = sp.GetRequiredService<IArmClient>();
        var put = await client.SendAsync(
            new ArmClientRequest("PUT", "/subscriptions/sub1", null, Headers("owner-key"), JsonNode.Parse("""{"properties":{"displayName":"s","state":"Enabled"}}""")),
            TestContext.Current.CancellationToken);
        put.StatusCode.ShouldBeOneOf(200, 201);

        var get = await client.SendAsync(
            new ArmClientRequest("GET", "/subscriptions/sub1", null, Headers("owner-key")),
            TestContext.Current.CancellationToken);
        get.StatusCode.ShouldBe(200);
        get.Body.ShouldNotBeNull();
        get.Body!.ToJsonString().ShouldContain("sub1");
    }

    [Fact]
    public async Task In_proc_client_without_a_key_is_401()
    {
        using var sp = CreateHost();
        var client = sp.GetRequiredService<IArmClient>();
        var resp = await client.SendAsync(
            new ArmClientRequest("GET", "/subscriptions/sub1"),
            TestContext.Current.CancellationToken);
        resp.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task In_proc_client_reader_key_write_is_403()
    {
        using var sp = CreateHost();
        var client = sp.GetRequiredService<IArmClient>();
        await client.SendAsync(
            new ArmClientRequest("PUT", "/subscriptions/sub1", null, Headers("owner-key"), JsonNode.Parse("""{"properties":{}}""")),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            new ArmClientRequest("PUT", "/subscriptions/sub1/resourceGroups/rg1", null, Headers("reader-key"), JsonNode.Parse("""{"properties":{}}""")),
            TestContext.Current.CancellationToken);
        resp.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task In_proc_client_surfaces_the_error_envelope()
    {
        using var sp = CreateHost();
        var client = sp.GetRequiredService<IArmClient>();
        var resp = await client.SendAsync(
            new ArmClientRequest("GET", "/subscriptions/missing", null, Headers("owner-key")),
            TestContext.Current.CancellationToken);
        resp.StatusCode.ShouldBe(404);
        resp.Error.ShouldNotBeNull();
        resp.Error!.Code.ShouldBe("SubscriptionNotFound");
        resp.Headers.ShouldContainKey("x-ms-error-code");
    }

    [Fact]
    public async Task In_proc_client_yields_no_events_for_a_non_data_plane_path()
    {
        using var sp = CreateHost();
        var client = sp.GetRequiredService<IArmClient>();
        var events = await Read(client, "/subscriptions/sub1/resourceGroups/rg1", lastEventId: null, count: 1, timeoutSeconds: 1);
        events.ShouldBeEmpty();
    }

    private static async Task<List<ArmSseEvent>> Read(IArmClient client, string path, string? lastEventId, int count, int timeoutSeconds = 5)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var events = new List<ArmSseEvent>();
        try
        {
            await foreach (var ev in client.StreamAsync(path, lastEventId, cts.Token))
            {
                events.Add(ev);
                if (events.Count == count)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // the tail is infinite; a short timeout is how a "no events" assertion ends
        }

        return events;
    }

    private static IReadOnlyDictionary<string, string> Headers(string key) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = key };

    private static ServiceProvider CreateHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-inproc-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Auth:ApiKeys:0:Key"] = "owner-key",
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Auth:ApiKeys:1:Key"] = "reader-key",
            ["Auth:ApiKeys:1:PrincipalId"] = "reader",
            ["Auth:ApiKeys:1:Roles:0"] = "Reader",
        }).Build();

        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
