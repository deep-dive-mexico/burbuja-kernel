using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.Arm.Rps;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

/// <summary>
/// K2 — the activity ring and the middleware that fills it.
///
/// <para><b>What these facts are about.</b> The row itself is uninteresting; where it is WRITTEN from and
/// what it is allowed to say are the whole design. The middleware sits before every other one so that it
/// records on the way out — with the request id, the correlation id, the principal, the resource id and the
/// final status all populated — and the projection snapshots on the way in so a caller never sees the row
/// for its own request. Both are asserted here, and both are the kind of property that looks fine until
/// somebody reorders the pipeline.</para>
/// </summary>
public sealed class ActivityLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A middleware that writes a fixed answer, so a fact can drive the ring without the whole kernel: what
    /// is under test is the ring and the recorder, not the router.
    /// </summary>
    private sealed class Answer(int order, int status, string? errorCode = null) : IArmMiddleware
    {
        public int Order => order;

        public ValueTask InvokeAsync(
            ArmRequestContext context,
            Func<ArmRequestContext, CancellationToken, ValueTask> next,
            CancellationToken ct)
        {
            // 600 stands in for "a handler ran": at that point the identity middleware has already set the
            // ids, which is exactly the ordering the real pipeline has.
            if (order == 600)
            {
                context.StatusCode = status;
                if (errorCode is not null)
                    context.Error = new ArmError(errorCode, errorCode, null, [], null, status);
                return ValueTask.CompletedTask;
            }

            return next(context, ct);
        }
    }

    private static (ArmPipeline Pipeline, InMemoryArmActivityLog Log) Build(params IArmMiddleware[] extra)
    {
        var log = new InMemoryArmActivityLog();
        var clock = new TimeProviderClock(TimeProvider.System);
        var middleware = new List<IArmMiddleware> { new IdentityProbe() };
        middleware.AddRange(extra);
        return (new ArmPipeline(
            middleware,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArmPipeline>.Instance,
            log,
            clock), log);
    }

    /// <summary>Stands in for the identity middleware at order 100: sets the ids and the principal.</summary>
    private sealed class IdentityProbe : IArmMiddleware
    {
        public int Order => 100;

        public ValueTask InvokeAsync(
            ArmRequestContext context,
            Func<ArmRequestContext, CancellationToken, ValueTask> next,
            CancellationToken ct)
        {
            context.RequestId = "req-1";
            context.CorrelationId = context.Headers.TryGetValue("x-ms-correlation-request-id", out var corr) ? corr : "corr-1";
            context.Principal = new ArmPrincipal("owner", "User", null, [], new Dictionary<string, string>());
            return next(context, ct);
        }
    }

    private sealed class Nothing : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static ArmRequestContext Request(string method = "GET", string path = "/subscriptions/local") =>
        new()
        {
            Method = method,
            Path = path,
            Services = new Nothing(),
            Headers = new Dictionary<string, string>(),
        };

    private static async Task<ArmRequestContext> RunAsync(ArmPipeline pipeline, ArmRequestContext context)
    {
        await pipeline.RunAsync(context, Ct);
        return context;
    }

    [Fact]
    public async Task One_request_appends_one_row_with_its_verb_and_status()
    {
        var (pipeline, log) = Build(new Answer(600, 200));

        await RunAsync(pipeline, Request("PUT", "/subscriptions/local/resourceGroups/dev"));

        var row = log.Recent().ShouldHaveSingleItem();
        row.Method.ShouldBe("PUT");
        row.Path.ShouldBe("/subscriptions/local/resourceGroups/dev");
        row.Status.ShouldBe(200);
        row.ErrorCode.ShouldBeNull();
        row.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// A request refused BEFORE any handler ran is the case the audit cannot see, and it is the reason this
    /// ring exists: RBAC refuses at order 600 and nothing downstream ever writes an event.
    /// </summary>
    [Fact]
    public async Task A_refused_request_is_recorded_even_though_nothing_downstream_ran()
    {
        var (pipeline, log) = Build(new Answer(600, 403, "AuthorizationFailed"));

        await RunAsync(pipeline, Request("PUT", "/subscriptions/local/providers/Burbuja.Agents/agents/a1"));

        var row = log.Recent().ShouldHaveSingleItem();
        row.Status.ShouldBe(403);
        row.ErrorCode.ShouldBe("AuthorizationFailed");
        row.PrincipalId.ShouldBe("owner");
    }

    /// <summary>
    /// The row carries the correlation id the CALLER sent, so a request can be traced from a portal click
    /// through ARM to the plugin that handled it.
    /// </summary>
    [Fact]
    public async Task The_row_carries_the_callers_correlation_id_and_its_principal()
    {
        var (pipeline, log) = Build(new Answer(600, 200));
        var request = new ArmRequestContext
        {
            Method = "GET",
            Path = "/subscriptions/local/providers/Burbuja.Resources/activity",
            Services = new Nothing(),
            Headers = new Dictionary<string, string> { ["x-ms-correlation-request-id"] = "trace-42" },
        };
        await RunAsync(pipeline, request);

        var row = log.Recent().ShouldHaveSingleItem();
        row.CorrelationId.ShouldBe("trace-42");
        row.RequestId.ShouldBe("req-1");
        row.PrincipalId.ShouldBe("owner");
        row.PrincipalType.ShouldBe("User");
    }

    /// <summary>A request that THREW is recorded too — it is the one an operator asks about first.</summary>
    [Fact]
    public async Task A_request_that_threw_is_recorded_with_the_error_the_caller_saw()
    {
        var (pipeline, log) = Build(new Thrower(600));

        var context = await RunAsync(pipeline, Request("DELETE", "/subscriptions/local/x"));

        context.Error.ShouldNotBeNull();
        var row = log.Recent().ShouldHaveSingleItem();
        row.Status.ShouldBe(500, "the row must show the status the caller was answered with, not the 200 default");
        row.ErrorCode.ShouldBe("InternalServerError");
    }

    private sealed class Thrower(int order) : IArmMiddleware
    {
        public int Order => order;

        public ValueTask InvokeAsync(
            ArmRequestContext context,
            Func<ArmRequestContext, CancellationToken, ValueTask> next,
            CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>
    /// The ring is bounded and keeps the NEWEST, and <c>Total</c> keeps counting past the bound — a reader
    /// who cannot tell a full ring from a quiet host will draw the wrong conclusion from the same screen.
    /// </summary>
    [Fact]
    public void The_ring_is_bounded_keeps_the_newest_and_counts_everything()
    {
        var log = new InMemoryArmActivityLog(capacity: 3);
        log.Capacity.ShouldBe(3);

        for (var i = 0; i < 10; i++)
            log.Record(Row($"req-{i}"));

        var rows = log.Recent();
        rows.Count.ShouldBe(3);
        rows.Select(r => r.RequestId).ShouldBe(["req-7", "req-8", "req-9"], ignoreOrder: false);
        log.Total.ShouldBe(10, "the ring drops rows; the count does not");
    }

    [Fact]
    public void The_ring_filters_by_principal_and_since()
    {
        var log = new InMemoryArmActivityLog(capacity: 50);
        var t0 = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        log.Record(Row("a", "owner", t0));
        log.Record(Row("b", "reader", t0.AddMinutes(1)));
        log.Record(Row("c", "owner", t0.AddMinutes(2)));

        log.Recent(principalId: "owner").Select(r => r.RequestId).ShouldBe(["a", "c"]);
        log.Recent(since: t0.AddMinutes(1)).Select(r => r.RequestId).ShouldBe(["b", "c"]);
        log.Recent(principalId: "owner", since: t0.AddMinutes(1)).Select(r => r.RequestId).ShouldBe(["c"]);
        log.Recent(principalId: "nobody").ShouldBeEmpty();
    }

    /// <summary>
    /// A read of the activity list never contains its own row: the list is snapshotted while the row is
    /// appended on the way out. Asserted against the middleware chain, because it is a property of the
    /// ORDER, not of either piece.
    /// </summary>
    [Fact]
    public async Task A_read_of_the_list_never_contains_its_own_row()
    {
        var log = new InMemoryArmActivityLog();
        var clock = new TimeProviderClock(TimeProvider.System);
        var handler = new ActivityHandler(log);
        var pipeline = new ArmPipeline(
            [new IdentityProbe(), new ListActivity(600, handler)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArmPipeline>.Instance,
            log,
            clock);

        // One request first, so the list has something in it.
        await RunAsync(pipeline, Request());
        // Then the read itself, which snapshots inside the handler and is recorded after.
        var read = await RunAsync(pipeline, Request("GET", "/subscriptions/local/providers/Burbuja.Resources/activity"));

        var page = read.Result.ShouldBeAssignableTo<PagedResult<ResourceEnvelope<ArmActivityRow>>>();
        page!.Value.Count.ShouldBe(1, "the read must see the request before it, and not itself");
        log.Total.ShouldBe(2, "and the read IS recorded — it just arrives after its own answer");
        log.Recent().Count.ShouldBe(2);
    }

    private sealed class ListActivity(int order, ActivityHandler handler) : IArmMiddleware
    {
        public int Order => order;

        public async ValueTask InvokeAsync(
            ArmRequestContext context,
            Func<ArmRequestContext, CancellationToken, ValueTask> next,
            CancellationToken ct)
        {
            var req = new ResourceListRequest
            {
                Id = new ResourceId
                {
                    SubscriptionId = "local",
                    Namespace = ArmConstants.ResourcesNamespace,
                    TypeSegments = ["activity"],
                },
                ApiVersion = "2026-09-01",
                Principal = new ArmPrincipal("owner", "User", null, [], new Dictionary<string, string>()),
                RequestId = context.RequestId ?? "",
                Services = context.Services,
                Top = 50,
            };
            context.Result = await handler.ListAsync(req, ct);
        }
    }

    /// <summary>The projection is read-only, and says why: an editable activity log is not a log.</summary>
    [Fact]
    public async Task The_activity_resource_refuses_writes_in_both_directions()
    {
        var handler = new ActivityHandler(new InMemoryArmActivityLog());
        var req = new ResourceRequest
        {
            Id = new ResourceId
            {
                SubscriptionId = "local",
                Namespace = ArmConstants.ResourcesNamespace,
                TypeSegments = ["activity"],
                NameSegments = ["req-1"],
            },
            ApiVersion = "2026-09-01",
            Principal = new ArmPrincipal("owner", "User", null, [], new Dictionary<string, string>()),
            RequestId = "req-x",
            Services = new Nothing(),
        };

        var put = await handler.PutAsync(req, Row("req-1"), Ct);
        put.ShouldBeOfType<PutOutcome<ArmActivityRow>.Rejected>().Error.Message.ShouldContain("not a thing to file");
        var delete = await handler.DeleteAsync(req, Ct);
        delete.ShouldBeOfType<DeleteOutcome.Rejected>().Error.Message.ShouldContain("expire with the ring");
    }

    /// <summary>
    /// A row is identified by its REQUEST, so a link keeps meaning the same request once the ring has
    /// dropped others. Naming it by its position in the window is how a portal opens somebody else's row.
    /// </summary>
    [Fact]
    public async Task A_row_is_found_by_its_request_id()
    {
        var log = new InMemoryArmActivityLog(capacity: 2);
        log.Record(Row("req-1"));
        log.Record(Row("req-2"));
        log.Record(Row("req-3"));   // evicts req-1

        var handler = new ActivityHandler(log);
        ResourceRequest Req(string name) => new()
        {
            Id = new ResourceId
            {
                SubscriptionId = "local",
                Namespace = ArmConstants.ResourcesNamespace,
                TypeSegments = ["activity"],
                NameSegments = [name],
            },
            ApiVersion = "2026-09-01",
            Principal = new ArmPrincipal("owner", "User", null, [], new Dictionary<string, string>()),
            RequestId = "req-x",
            Services = new Nothing(),
        };

        (await handler.GetAsync(Req("req-3"), Ct))!.Properties.RequestId.ShouldBe("req-3");
        (await handler.GetAsync(Req("req-1"), Ct)).ShouldBeNull("an evicted row is gone, and says so");
    }

    /// <summary>The descriptors advertise exactly what the handler serves, including that it is read-only.</summary>
    [Fact]
    public void The_activity_descriptor_is_read_only_and_untracked()
    {
        var descriptor = new ResourcesProvider().ResourceTypes.Single(t => t.Type == "activity");

        descriptor.IsTracked.ShouldBeFalse("the ring is the state; a stored copy would be a copy that drifts");
        descriptor.Actions.ShouldBeEmpty("an action on a log is a way to write one");
        descriptor.Capabilities.HasFlag(ResourceCapabilities.Get).ShouldBeTrue();
        descriptor.Capabilities.HasFlag(ResourceCapabilities.List).ShouldBeTrue();
        descriptor.Capabilities.HasFlag(ResourceCapabilities.Put).ShouldBeFalse();
        descriptor.Capabilities.HasFlag(ResourceCapabilities.Delete).ShouldBeFalse();
    }

    private static ArmActivityRow Row(string requestId, string principal = "owner", DateTimeOffset? at = null) =>
        new(
            At: at ?? DateTimeOffset.UtcNow,
            DurationMs: 1.5,
            Method: "GET",
            Path: "/subscriptions/local",
            ResourceId: "/subscriptions/local",
            PrincipalId: principal,
            PrincipalType: "User",
            ApiVersion: "2026-09-01",
            Status: 200,
            ErrorCode: null,
            RequestId: requestId,
            CorrelationId: "corr",
            IsDataPlane: false);
}
