using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Burbuja.Core.Arm.Rps;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

public sealed class ArmPipelineTests
{
    [Fact]
    public async Task Missing_api_version_returns_400()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups/rg1", query: new Dictionary<string, string>());
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("MissingApiVersionParameter");
        ctx.ResponseHeaders["x-ms-error-code"].ShouldBe("MissingApiVersionParameter");
    }

    [Fact]
    public async Task AuthorizationFailed_returns_403()
    {
        using var sp = CreateHost(reader: true);
        await SeedSubscription(sp);
        var ctx = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""", key: "reader-key");
        ctx.StatusCode.ShouldBe(403);
        ctx.Error!.Code.ShouldBe("AuthorizationFailed");
    }

    [Fact]
    public async Task Stale_etag_returns_412()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var created = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""", extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "create-rg" });
        created.StatusCode.ShouldBeOneOf(200, 201);
        var stale = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""", extraHeaders: new Dictionary<string, string>
        {
            ["If-Match"] = "W/\"999\"",
            ["x-ms-client-request-id"] = "stale-rg",
        });
        stale.StatusCode.ShouldBe(412);
        stale.Error!.Code.ShouldBe("PreconditionFailed");
    }

    [Fact]
    public async Task Throttle_returns_429_with_Retry_After()
    {
        using var sp = CreateHost(writesPerMinute: 2);
        await SeedSubscription(sp);
        var first = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""");
        first.StatusCode.ShouldBeOneOf(200, 201);
        var second = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg2", body: """{"properties":{}}""");
        second.StatusCode.ShouldBe(429);
        second.ResponseHeaders.ShouldContainKey("Retry-After");
        second.Error!.Code.ShouldBe("TooManyRequests");
    }

    [Fact]
    public async Task Async_put_returns_202_with_Azure_AsyncOperation()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var ctx = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/lock1", body: """{"properties":{"level":"CanNotDelete"}}""");
        ctx.StatusCode.ShouldBe(202);
        ctx.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");
        ctx.ResponseHeaders["Azure-AsyncOperation"].ShouldContain("/operations/");
        ctx.ResponseHeaders.ShouldContainKey("Location");
        ctx.ResponseHeaders.ShouldContainKey("Retry-After");
    }

    [Fact]
    public async Task List_returns_value_and_nextLink()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg2", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$top"] = "1",
        });
        ctx.StatusCode.ShouldBe(200);
        var obj = ctx.Result.ShouldBeOfType<JsonObject>();
        obj.ContainsKey("value").ShouldBeTrue();
        obj.ContainsKey("nextLink").ShouldBeTrue();
        obj["nextLink"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        obj["value"]!.AsArray().Count.ShouldBe(1);
    }

    private static async Task SeedSubscription(ServiceProvider sp)
    {
        var ctx = await Send(sp, "PUT", "/subscriptions/sub1", body: """{"properties":{"displayName":"s","state":"Enabled"}}""");
        ctx.Error?.Code.ShouldBeNull($"seed failed: {ctx.Error?.Code} {ctx.Error?.Message}");
        ctx.StatusCode.ShouldBeOneOf(200, 201);
    }

    private static ServiceProvider CreateHost(bool reader = false, int writesPerMinute = 1200)
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
            ["Auth:ApiKeys:1:Key"] = "reader-key",
            ["Auth:ApiKeys:1:PrincipalId"] = "reader",
            ["Auth:ApiKeys:1:Roles:0"] = "Reader",
            ["Arm:Throttle:WritesPerMinute"] = writesPerMinute.ToString(),
            ["Arm:Throttle:ReadsPerMinute"] = "12000",
            ["Arm:Throttle:DeletesPerMinute"] = "1200",
        }).Build();
        _ = reader;
        var services = new ServiceCollection();
        services.AddBurbujaCore(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task<ArmRequestContext> Send(
        IServiceProvider sp,
        string method,
        string path,
        Dictionary<string, string>? query = null,
        string? body = null,
        string key = "owner-key",
        Dictionary<string, string>? extraHeaders = null,
        Stream? bodyStream = null)
    {
        query ??= new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = key,
        };
        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
                headers[kv.Key] = kv.Value;
        }

        Stream? stream = bodyStream ?? (body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body)));
        var ctx = new ArmRequestContext
        {
            Method = method,
            Path = path,
            Query = query,
            Headers = headers,
            Body = stream,
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Get_returns_etag()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var get = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups/rg1");
        get.StatusCode.ShouldBe(200);
        get.ResponseHeaders.ShouldContainKey("ETag");
        get.ResponseHeaders["ETag"].ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Malformed_skip_token_returns_400()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$skipToken"] = "%%%not-base64%%%",
        });
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("InvalidSkipToken");
    }

    [Fact]
    public async Task Filter_name_eq_narrows_list()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/alpha", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/beta", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$filter"] = "name eq 'alpha'",
        });
        ctx.StatusCode.ShouldBe(200);
        var obj = ctx.Result.ShouldBeOfType<JsonObject>();
        obj["value"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task If_match_star_requires_existing_resource()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var missing = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/nope", body: """{"properties":{}}""", extraHeaders: new Dictionary<string, string>
        {
            ["If-Match"] = "*",
            ["x-ms-client-request-id"] = "if-match-star",
        });
        missing.StatusCode.ShouldBe(412);
    }

    [Fact]
    public async Task ReadOnly_lock_returns_409_on_write()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var lockPut = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/ro", body: """{"properties":{"level":"ReadOnly"}}""");
        lockPut.StatusCode.ShouldBe(202);
        var patch = await Send(sp, "PATCH", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""");
        patch.StatusCode.ShouldBe(409);
        patch.Error!.Code.ShouldBe("ScopeLocked");
    }

    [Fact]
    public async Task CanNotDelete_lock_returns_409_on_delete()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var lockPut = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/cnd", body: """{"properties":{"level":"CanNotDelete"}}""");
        lockPut.StatusCode.ShouldBe(202);
        var del = await Send(sp, "DELETE", "/subscriptions/sub1/resourceGroups/rg1");
        del.StatusCode.ShouldBe(409);
        del.Error!.Code.ShouldBe("ScopeLocked");
    }

    [Fact]
    public async Task Patch_without_capability_returns_405()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var put = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/lock1", body: """{"properties":{"level":"CanNotDelete"}}""");
        put.StatusCode.ShouldBe(202);
        var patch = await Send(sp, "PATCH", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/lock1", body: """{"properties":{"level":"ReadOnly"}}""");
        patch.StatusCode.ShouldBe(405);
        patch.Error!.Code.ShouldBe("MethodNotAllowed");
    }

    [Fact]
    public async Task Child_list_is_scoped_to_parent()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg2", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/a", body: """{"properties":{"level":"CanNotDelete"}}""")).StatusCode.ShouldBe(202);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg2/providers/Burbuja.Authorization/locks/b", body: """{"properties":{"level":"CanNotDelete"}}""")).StatusCode.ShouldBe(202);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks");
        ctx.StatusCode.ShouldBe(200);
        var obj = ctx.Result.ShouldBeOfType<JsonObject>();
        var value = obj["value"]!.AsArray();
        value.Count.ShouldBe(1);
        value[0]!["name"]!.GetValue<string>().ShouldBe("a");
    }

    [Fact]
    public async Task If_none_match_star_rejects_overwrite()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var again = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""", extraHeaders: new Dictionary<string, string>
        {
            ["If-None-Match"] = "*",
            ["x-ms-client-request-id"] = "if-none-match-star",
        });
        again.StatusCode.ShouldBe(412);
    }

    [Fact]
    public async Task Idempotency_replay_strips_identity_headers_and_clones()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var first = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""", extraHeaders: new Dictionary<string, string>
        {
            ["x-ms-client-request-id"] = "idem-1",
        });
        first.StatusCode.ShouldBeOneOf(200, 201);
        var firstRid = first.ResponseHeaders["x-ms-request-id"];
        if (first.Result is JsonObject mutated)
            mutated["tampered"] = true;

        var replay = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""", extraHeaders: new Dictionary<string, string>
        {
            ["x-ms-client-request-id"] = "idem-1",
        });
        replay.StatusCode.ShouldBe(first.StatusCode);
        replay.ResponseHeaders["x-ms-request-id"].ShouldNotBe(firstRid);
        ReferenceEquals(first.Result, replay.Result).ShouldBeFalse();
        if (replay.Result is JsonObject clone)
            clone.ContainsKey("tampered").ShouldBeFalse();

        var conflict = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{"other":true}}""", extraHeaders: new Dictionary<string, string>
        {
            ["x-ms-client-request-id"] = "idem-1",
        });
        conflict.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Non_seekable_body_is_buffered_once()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var bytes = Encoding.UTF8.GetBytes("""{"properties":{}}""");
        var ctx = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg-ns", bodyStream: new NonSeekableStream(bytes));
        ctx.StatusCode.ShouldBeOneOf(200, 201);
        var get = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups/rg-ns");
        get.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Skip_token_pages_without_overlap()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg2", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var page1 = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$top"] = "1",
        });
        page1.StatusCode.ShouldBe(200);
        var obj = page1.Result.ShouldBeOfType<JsonObject>();
        var next = obj["nextLink"]!.GetValue<string>();
        var token = next[(next.IndexOf("$skipToken=", StringComparison.Ordinal) + "$skipToken=".Length)..].Split('&')[0];
        token = Uri.UnescapeDataString(token);
        var page2 = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$top"] = "1",
            ["$skipToken"] = token,
        });
        page2.StatusCode.ShouldBe(200);
        var obj2 = page2.Result.ShouldBeOfType<JsonObject>();
        var n1 = obj["value"]![0]!["name"]!.GetValue<string>();
        var n2 = obj2["value"]![0]!["name"]!.GetValue<string>();
        n1.ShouldNotBe(n2);
    }

    [Fact]
    public async Task Unauthorized_returns_401_with_www_authenticate()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/subscriptions/sub1", key: "nope");
        ctx.StatusCode.ShouldBe(401);
        ctx.ResponseHeaders["WWW-Authenticate"].ShouldBe("Bearer, ApiKey");
    }

    [Fact]
    public async Task Async_operation_is_routable_and_operationResults_returns_target()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var put = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/lock1", body: """{"properties":{"level":"CanNotDelete"}}""");
        put.StatusCode.ShouldBe(202);
        var opUrl = put.ResponseHeaders["Azure-AsyncOperation"];
        var opPath = opUrl.Split('?')[0];
        opPath.ShouldContain("/locations/local/operations/");
        var op = await Send(sp, "GET", opPath);
        op.StatusCode.ShouldBe(200);
        var resultPath = opPath.Replace("/operations/", "/operationResults/", StringComparison.Ordinal);
        var result = await Send(sp, "GET", resultPath);
        result.StatusCode.ShouldBe(200);
        var env = result.Result.ShouldBeAssignableTo<IResourceEnvelope>();
        env!.Name.ShouldBe("lock1");
    }

    [Fact]
    public async Task OperationResults_running_returns_202_failed_returns_error()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ops = sp.GetRequiredService<IOperationTracker>();
        await ops.StartAsync("run1", "sub1", "Burbuja.Authorization", "local", "/subscriptions/sub1/resourceGroups/rg1", null, CancellationToken.None);
        var running = await Send(sp, "GET", "/subscriptions/sub1/providers/Burbuja.Authorization/locations/local/operationResults/run1");
        running.StatusCode.ShouldBe(202);

        await ops.StartAsync("fail1", "sub1", "Burbuja.Authorization", "local", "/subscriptions/sub1/resourceGroups/rg1", null, CancellationToken.None);
        await ops.FailAsync("fail1", ArmError.Validation("boom", []), CancellationToken.None);
        var failed = await Send(sp, "GET", "/subscriptions/sub1/providers/Burbuja.Authorization/locations/local/operationResults/fail1");
        failed.StatusCode.ShouldBe(400);
        failed.Error!.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task Provider_register_completes_synchronously()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "POST", "/subscriptions/sub1/providers/Burbuja.Authorization/register");
        ctx.StatusCode.ShouldBe(200);
        var body = ctx.Result.ShouldBeOfType<JsonObject>();
        body["registrationState"]!.GetValue<string>().ShouldBe("Registered");
        var dir = sp.GetRequiredService<IArmDirectory>();
        dir.TryGetProviderRegistration("sub1", "Burbuja.Authorization", out var registration).ShouldBeTrue();
        registration.ShouldBe(ProviderRegistrationState.Registered);
    }

    [Fact]
    public async Task Resource_group_delete_removes_directory_entry()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "DELETE", "/subscriptions/sub1/resourceGroups/rg1")).StatusCode.ShouldBe(200);
        var lockPut = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/x", body: """{"properties":{"level":"CanNotDelete"}}""");
        lockPut.StatusCode.ShouldBe(404);
        lockPut.Error!.Code.ShouldBe("ResourceGroupNotFound");
    }

    [Fact]
    public async Task Warned_subscription_rejects_writes()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1", body: """{"properties":{"displayName":"s","state":"Warned"}}""", extraHeaders: new Dictionary<string, string>
        {
            ["x-ms-client-request-id"] = "warn-sub",
        })).StatusCode.ShouldBeOneOf(200, 201);
        var write = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""");
        write.StatusCode.ShouldBe(409);
        write.Error!.Code.ShouldBe("SubscriptionNotEnabled");
    }

    [Fact]
    public async Task Get_providers_lists_routing_table()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/providers");
        ctx.StatusCode.ShouldBe(200);
        var obj = ctx.Result.ShouldBeOfType<JsonObject>();
        var names = obj["value"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.ShouldContain("Burbuja.Resources");
        names.ShouldContain("Burbuja.Authorization");
    }

    [Fact]
    public async Task Get_provider_returns_manifest_without_system_type()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/providers/Burbuja.Authorization");
        ctx.StatusCode.ShouldBe(200);
        var env = ctx.Result.ShouldBeAssignableTo<IResourceEnvelope>();
        var json = JsonSerializer.Serialize(env, JsonWire.Options);
        json.ShouldNotContain("System.");
        json.ShouldNotContain("PropertiesType");
        var typed = ctx.Result.ShouldBeOfType<ResourceEnvelope<ProviderProperties>>();
        typed.Properties.Namespace.ShouldBe("Burbuja.Authorization");
        typed.Properties.ResourceTypes.ShouldNotBeEmpty();
        typed.Properties.ResourceTypes.ShouldContain(t => t.ResourceType == "locks");
        typed.Properties.ResourceTypes[0].Capabilities.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Get_provider_operations_lists_rbac_catalogue()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/providers/Burbuja.Authorization/operations");
        ctx.StatusCode.ShouldBe(200);
        var obj = ctx.Result.ShouldBeOfType<JsonObject>();
        var names = obj["value"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToArray();
        names.ShouldContain("Burbuja.Authorization/locks/read");
        names.ShouldContain("Burbuja.Authorization/locks/write");
    }

    [Fact]
    public async Task Unknown_subscription_returns_404()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/subscriptions/no-such");
        ctx.StatusCode.ShouldBe(404);
        ctx.Error!.Code.ShouldBe("SubscriptionNotFound");
    }

    [Fact]
    public async Task Suspended_subscription_rejects_writes()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1", body: """{"properties":{"displayName":"s","state":"Suspended"}}""", extraHeaders: new Dictionary<string, string>
        {
            ["x-ms-client-request-id"] = "suspend-sub",
        })).StatusCode.ShouldBeOneOf(200, 201);
        var write = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""");
        write.StatusCode.ShouldBe(409);
        write.Error!.Code.ShouldBe("SubscriptionNotEnabled");
    }

    [Fact]
    public async Task Collection_post_returns_405()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "POST", "/subscriptions/sub1/resourceGroups", body: """{"properties":{}}""");
        ctx.StatusCode.ShouldBe(405);
        ctx.Error!.Code.ShouldBe("MethodNotAllowed");
    }

    [Fact]
    public async Task Invalid_properties_type_returns_schema_details()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":[]}""");
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("InvalidRequestContent");
        ctx.Error.Details.ShouldNotBeEmpty();
        (ctx.Error.Details[0].Target ?? "").ShouldContain("/properties");
    }

    [Fact]
    public async Task Patch_resource_group_succeeds()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var patch = await Send(sp, "PATCH", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""");
        patch.StatusCode.ShouldBeOneOf(200, 201);
        patch.ResponseHeaders.ShouldContainKey("ETag");
    }

    [Fact]
    public async Task Every_response_has_request_id()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/providers");
        ctx.ResponseHeaders.ShouldContainKey("x-ms-request-id");
        ctx.ResponseHeaders["x-ms-request-id"].ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Invalid_api_version_returns_400()
    {
        using var sp = CreateHost();
        var ctx = await Send(sp, "GET", "/providers", query: new Dictionary<string, string> { ["api-version"] = "1999-01-01" });
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("InvalidApiVersionParameter");
    }

    [Fact]
    public async Task Missing_resource_group_for_child_returns_404()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups/ghost/providers/Burbuja.Authorization/locks/l1");
        ctx.StatusCode.ShouldBe(404);
        ctx.Error!.Code.ShouldBe("ResourceGroupNotFound");
    }

    [Fact]
    public async Task Filter_name_startswith_and_tags_eq()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/alpha", body: """{"tags":{"env":"dev"},"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/beta", body: """{"tags":{"env":"prod"},"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var starts = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$filter"] = "name startswith 'al'",
        });
        starts.StatusCode.ShouldBe(200);
        starts.Result.ShouldBeOfType<JsonObject>()["value"]!.AsArray().Count.ShouldBe(1);

        var tags = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$filter"] = "tags/env eq 'prod'",
        });
        tags.StatusCode.ShouldBe(200);
        tags.Result.ShouldBeOfType<JsonObject>()["value"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Filter_and_combines_clauses()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/alpha", body: """{"tags":{"env":"dev"},"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/alpine", body: """{"tags":{"env":"prod"},"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$filter"] = "name startswith 'al' and tags/env eq 'dev'",
        });
        ctx.StatusCode.ShouldBe(200);
        ctx.Result.ShouldBeOfType<JsonObject>()["value"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_filter_returns_400()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$filter"] = "name contains 'x'",
        });
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("InvalidFilter");
    }

    [Fact]
    public async Task Filter_properties_eq()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/ok", body: """{"properties":{"provisioningState":"Succeeded"}}""")).StatusCode.ShouldBeOneOf(200, 201);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/bad", body: """{"properties":{"provisioningState":"Failed"}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups", query: new Dictionary<string, string>
        {
            ["api-version"] = ArmConstants.ApiVersion,
            ["$filter"] = "properties/provisioningState eq 'Failed'",
        });
        ctx.StatusCode.ShouldBe(200);
        ctx.Result.ShouldBeOfType<JsonObject>()["value"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_lock_after_put()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        (await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", body: """{"properties":{}}""")).StatusCode.ShouldBeOneOf(200, 201);
        var put = await Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/lock1", body: """{"properties":{"level":"CanNotDelete"}}""");
        put.StatusCode.ShouldBe(202);
        var del = await Send(sp, "DELETE", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Authorization/locks/lock1");
        del.StatusCode.ShouldBeOneOf(200, 202, 204);
    }

    [Fact]
    public async Task Get_missing_resource_group_returns_404()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "GET", "/subscriptions/sub1/resourceGroups/missing");
        ctx.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Unregister_action_body_is_schema_validated()
    {
        using var sp = CreateHost();
        await SeedSubscription(sp);
        var ctx = await Send(sp, "POST", "/subscriptions/sub1/providers/Burbuja.Authorization/unregister", body: "{}");
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("InvalidRequestContent");
        ctx.Error.Details.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_with_request_id()
    {
        var pipeline = new ArmPipeline(
            [new RequestIdentityMiddleware(), new ThrowingMiddleware()],
            NullLogger<ArmPipeline>.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var ctx = new ArmRequestContext
        {
            Method = "GET",
            Path = "/subscriptions/sub1",
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string>(),
            Services = services,
        };
        await pipeline.RunAsync(ctx, CancellationToken.None);
        ctx.StatusCode.ShouldBe(500);
        ctx.Error!.Code.ShouldBe("InternalServerError");
        ctx.Error.Target.ShouldBe(ctx.RequestId);
        ctx.ResponseHeaders["x-ms-request-id"].ShouldBe(ctx.RequestId);
        var envelope = ctx.Result.ShouldBeOfType<ArmErrorEnvelope>();
        envelope.Error.Target.ShouldBe(ctx.RequestId);
    }

    private sealed class ThrowingMiddleware : IArmMiddleware
    {
        public int Order => 200;

        public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct) =>
            throw new InvalidOperationException("planted");
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, bytes.Length - _pos);
            Buffer.BlockCopy(bytes, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
