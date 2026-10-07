using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Applications;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.Redaction;
using Burbuja.Core.Sdk.Applications;
using Burbuja.Hosting.Applications;
using Burbuja.Plugins.Store.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Burbuja.Hosting.Tests;

public sealed class ConnectedApplicationTests
{
    private const string AlphaKey = "synthetic-alpha-application-key-0001";
    private const string BetaKey = "synthetic-beta-application-key-00002";
    private static readonly ApplicationScope AlphaScope = new("development", "example", "alpha");
    private static readonly ApplicationScope BetaScope = new("development", "example", "beta");
    private const string Resource = "/subscriptions/local/resourceGroups/dev/providers/Example.Connected/notes/same";

    [Fact]
    public async Task Same_resource_event_artifact_and_lease_identifiers_are_isolated_over_real_sockets()
    {
        await using var server = await Server.Start();
        var a = await server.Client(AlphaKey, AlphaScope);
        var b = await server.Client(BetaKey, BetaScope);
        var ae = await a.CreateAsync(Envelope("alpha"), TestContext.Current.CancellationToken);
        var be = await b.CreateAsync(Envelope("beta"), TestContext.Current.CancellationToken);
        Assert.Equal("alpha", (await ((IResourceStore)a).GetAsync(Resource.ToLowerInvariant(), TestContext.Current.CancellationToken))!.Properties["value"]!.GetValue<string>());
        Assert.Equal("beta", (await ((IResourceStore)b).GetAsync(Resource.ToLowerInvariant(), TestContext.Current.CancellationToken))!.Properties["value"]!.GetValue<string>());
        // Equal version tokens are valid in independent scopes; they must not join the stores.
        Assert.Equal(ae.ETag, be.ETag);
        Assert.Equal(1, await a.AppendAsync(Event("alpha"), TestContext.Current.CancellationToken));
        Assert.Equal(1, await b.AppendAsync(Event("beta"), TestContext.Current.CancellationToken));
        var alphaEvents = new List<LedgerEvent>();
        await foreach (var evt in a.ReadRangeAsync("same", 0, long.MaxValue, 0, null, TestContext.Current.CancellationToken)) alphaEvents.Add(evt);
        Assert.Single(alphaEvents);
        Assert.Equal("alpha", alphaEvents[0].Summary);
        Assert.Null(EventChain.Verify(alphaEvents));
        await a.PutAsync("same.txt", new MemoryStream(Encoding.UTF8.GetBytes("alpha")), "text/plain", null, TestContext.Current.CancellationToken);
        await b.PutAsync("same.txt", new MemoryStream(Encoding.UTF8.GetBytes("beta")), "text/plain", null, TestContext.Current.CancellationToken);
        var blob = await ((IArtifactStore)a).GetAsync("same.txt", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(blob!.Content);
        Assert.Equal("alpha", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await a.AcquireAsync("same", "owner", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.NotNull(await b.AcquireAsync("same", "owner", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.Null(await a.AcquireAsync("same", "other", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        var artifactDirectories = Directory.GetDirectories(server.Directory).Where(d => Path.GetFileName(d) != "_control").ToArray();
        Assert.Equal(2, artifactDirectories.Length);
    }

    [Fact]
    public async Task Concurrent_atomic_creates_and_conditional_updates_have_one_winner()
    {
        await using var server = await Server.Start();
        var a = await server.Client(AlphaKey, AlphaScope);
        async Task<int> Create()
        {
            try { await a.CreateAsync(Envelope("value"), TestContext.Current.CancellationToken); return 200; }
            catch (ArmException error) { return error.Error.HttpStatus; }
        }
        var creates = await Task.WhenAll(Create(), Create());
        Assert.Equal(new[] { 200, 412 }, creates.Order().ToArray());
        var resource = await ((IResourceStore)a).GetAsync(Resource.ToLowerInvariant(), TestContext.Current.CancellationToken);
        async Task<int> Update(string value)
        {
            try { await a.UpsertAsync(resource! with { Properties = new JsonObject { ["value"] = value } }, resource!.ETag, TestContext.Current.CancellationToken); return 200; }
            catch (ArmException error) { return error.Error.HttpStatus; }
        }
        var updates = await Task.WhenAll(Update("a"), Update("b"));
        Assert.Equal(new[] { 200, 412 }, updates.Order().ToArray());
    }

    [Fact]
    public async Task Replayed_event_write_has_one_booking_identity_and_survives_kernel_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "connected-test-" + Guid.NewGuid().ToString("N"));
        var call = new ApplicationStorageCall("events.append", new JsonObject { ["event"] = JsonSerializer.SerializeToNode(Event("once"), JsonWire.Options) }, Guid.NewGuid().ToString("N"), "human");
        await using (var server = await Server.Start(directory))
        {
            var a = await server.Client(AlphaKey, AlphaScope);
            var first = await a.ExecuteAsync(call, TestContext.Current.CancellationToken);
            var second = await a.ExecuteAsync(call with { RequestId = call.RequestId.ToUpperInvariant() }, TestContext.Current.CancellationToken);
            Assert.Equal(first.Value!.GetValue<long>(), second.Value!.GetValue<long>());
            Assert.Equal("completed", (await a.ReceiptAsync(call.RequestId, TestContext.Current.CancellationToken))!["state"]!.GetValue<string>());
            var b = await server.Client(BetaKey, BetaScope);
            Assert.Null(await b.ReceiptAsync(call.RequestId, TestContext.Current.CancellationToken));
        }
        await using (var server = await Server.Start(directory))
        {
            var a = await server.Client(AlphaKey, AlphaScope);
            Assert.Equal(1, (await a.ExecuteAsync(call, TestContext.Current.CancellationToken)).Value!.GetValue<long>());
            var events = new List<LedgerEvent>();
            await foreach (var evt in a.ReadRangeAsync("same", 0, long.MaxValue, 0, null, TestContext.Current.CancellationToken)) events.Add(evt);
            Assert.Single(events);
            Assert.Null(EventChain.Verify(events));
        }
        System.IO.Directory.Delete(directory, true);
    }

    [Fact]
    public async Task Credentials_cannot_select_another_application_or_environment_and_readonly_writes_fail()
    {
        await using var server = await Server.Start(readonlyBeta: true);
        await Assert.ThrowsAnyAsync<Exception>(async () => await server.Client(AlphaKey, BetaScope));
        await Assert.ThrowsAnyAsync<Exception>(async () => await server.Client(AlphaKey, AlphaScope with { Environment = "production" }));
        var b = await server.Client(BetaKey, BetaScope);
        var failure = await Assert.ThrowsAsync<ArmException>(async () => await b.CreateAsync(Envelope("forbidden"), TestContext.Current.CancellationToken));
        Assert.Equal(403, failure.Error.HttpStatus);
        using var raw = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.GetAsync(server.Url + "/kernel/v1/handshake", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Live_event_tail_observes_its_scope_and_cancellation()
    {
        await using var server = await Server.Start();
        var a = await server.Client(AlphaKey, AlphaScope);
        var b = await server.Client(BetaKey, BetaScope);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var tail = a.TailAsync("same", 0, deadline.Token).GetAsyncEnumerator(deadline.Token);
        var next = tail.MoveNextAsync().AsTask();
        await b.AppendAsync(Event("beta"), deadline.Token);
        await a.AppendAsync(Event("alpha"), deadline.Token);
        Assert.True(await next);
        Assert.Equal("alpha", tail.Current.Summary);
        var waiting = tail.MoveNextAsync().AsTask();
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public async Task Auxiliary_state_and_usage_resources_do_not_join_between_applications()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await Server.Start();
        var a = await server.Client(AlphaKey, AlphaScope);
        var b = await server.Client(BetaKey, BetaScope);
        Assert.True(a.TryBegin("same"));
        Assert.False(a.TryBegin("same"));
        Assert.True(b.TryBegin("same"));
        a.End("same");
        b.End("same");
        a.Put("same", new IdempotencyRecord("hash", new JsonObject { ["owner"] = "alpha" }, 200,
            new Dictionary<string, string>(), DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Null(((IIdempotencyStore)b).Get("same"));
        Assert.Equal("alpha", ((IIdempotencyStore)a).Get("same")!.Response!["owner"]!.GetValue<string>());
        await a.DecisionCache<string>().SetAsync("same", "same", "alpha", ct);
        Assert.Null(await b.DecisionCache<string>().GetAsync("same", "same", ct));
        Assert.Equal("alpha", await a.DecisionCache<string>().GetAsync("same", "same", ct));
        var messageId = Guid.NewGuid();
        await a.EnqueueAsync(new OutboxMessage(messageId, messageId, "example.task", new JsonObject { ["owner"] = "alpha" }), ct);
        await b.EnqueueAsync(new OutboxMessage(messageId, messageId, "example.task", new JsonObject { ["owner"] = "beta" }), ct);
        Assert.Equal("alpha", (await a.ClaimAsync(1, DateTimeOffset.UtcNow, ct)).Single().Message.Payload["owner"]!.GetValue<string>());
        Assert.Equal("beta", (await b.ClaimAsync(1, DateTimeOffset.UtcNow, ct)).Single().Message.Payload["owner"]!.GetValue<string>());
        await a.StartAsync("same", "local", "Example.Connected", "local", Resource, new JsonObject { ["owner"] = "alpha" }, ct);
        Assert.Null(await ((IOperationTracker)b).GetAsync("same", ct));
        await b.StartAsync("same", "local", "Example.Connected", "local", Resource, new JsonObject { ["owner"] = "beta" }, ct);
        await a.SucceedAsync("same", null, ct);
        Assert.Equal(OperationStatus.Succeeded, (await ((IOperationTracker)a).GetAsync("same", ct))!.Status);
        Assert.Equal(OperationStatus.Running, (await ((IOperationTracker)b).GetAsync("same", ct))!.Status);
        var request = new ApprovalRequest("same", null, ApprovalKind.Permission, "tool", Resource, "same", null,
            "test", "write", null, "test", [], false, TimeSpan.FromMinutes(1));
        server.Stores(AlphaKey).Approvals.Put(new ApprovalRecord("same", request, null, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1), null, null));
        Assert.NotNull(((IApprovalStore)a).Get("same"));
        Assert.Null(((IApprovalStore)b).Get("same"));
    }

    [Fact]
    public async Task Persistence_credentials_cannot_forge_kernel_approvals_runtime_resources_or_events()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await Server.Start();
        var a = await server.Client(AlphaKey, AlphaScope);
        var runtime = Envelope("forged") with { Id = "/providers/Burbuja.Agents/ledgers/same", Type = "Burbuja.Agents/ledgers" };
        Assert.Equal(403, (await Assert.ThrowsAsync<ArmException>(async () => await a.CreateAsync(runtime, ct))).Error.HttpStatus);
        Assert.Equal(403, (await Assert.ThrowsAsync<ArmException>(async () => await a.AppendAsync(Event("forged") with { Type = "agents.activation.settled" }, ct))).Error.HttpStatus);
        Assert.Equal(403, (await Assert.ThrowsAsync<ArmException>(async () => await a.ExecuteAsync(new ApplicationStorageCall(
            "approvals.putGrant", new JsonObject(), Guid.NewGuid().ToString("N")), ct))).Error.HttpStatus);
        await server.Stores(AlphaKey).Operations.StartAsync("kernel-op", "local", "Burbuja.Agents", "local", null, null, ct);
        Assert.Equal(403, (await Assert.ThrowsAsync<ArmException>(async () => await a.StartAsync("kernel-op", "local",
            "Example.Connected", "local", null, null, ct))).Error.HttpStatus);
        Assert.Equal("Running", (await server.Stores(AlphaKey).Operations.GetAsync("kernel-op", ct))!.Status.ToString());
    }

    [Fact]
    public async Task A_second_kernel_cannot_open_the_same_managed_writer_binding()
    {
        await using var server = await Server.Start();
        await Assert.ThrowsAsync<IOException>(async () => await Server.Start(server.Directory));
    }

    [Fact]
    public async Task Interrupted_effect_is_fenced_as_unknown_across_restart_instead_of_running_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "connected-unknown-" + Guid.NewGuid().ToString("N"));
        var call = new ApplicationStorageCall("events.append", new JsonObject { ["event"] = JsonSerializer.SerializeToNode(Event("once"), JsonWire.Options) }, Guid.NewGuid().ToString("N"));
        await using (var server = await Server.Start(directory, faultAppend: true))
        {
            var a = await server.Client(AlphaKey, AlphaScope);
            var unavailable = await Assert.ThrowsAsync<KernelUnavailableException>(async () => await a.ExecuteAsync(call, ct));
            Assert.Equal(call.RequestId, unavailable.RequestId);
            Assert.Equal("started", (await a.ReceiptAsync(call.RequestId, ct))!["state"]!.GetValue<string>());
        }
        await using (var server = await Server.Start(directory))
        {
            var a = await server.Client(AlphaKey, AlphaScope);
            var unknown = await Assert.ThrowsAsync<ArmException>(async () => await a.ExecuteAsync(call, ct));
            Assert.Equal("OutcomeUnknown", unknown.Error.Code);
            var events = new List<LedgerEvent>();
            await foreach (var evt in a.ReadRangeAsync("same", 0, long.MaxValue, 0, null, ct)) events.Add(evt);
            Assert.Single(events);
            Assert.Null(EventChain.Verify(events));
        }
        Directory.Delete(directory, true);
    }

    private static ResourceEnvelope<JsonObject> Envelope(string value) => new(Resource, "same", "Example.Connected/notes", null,
        new Dictionary<string, string>(), new JsonObject { ["value"] = value }, ProvisioningState.Succeeded,
        new SystemData(null, "human", "User", null, null, null), "");
    private static EventAppend Event(string summary) => new("same", null, "example.test", summary, null, RenderLane.Log, null, null, null);

    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<HttpClient> _clients = [];
        private readonly bool _temporary;
        public string Directory { get; }
        public string Url => _app.Urls.Single();
        private Server(WebApplication app, string directory, bool temporary) { _app = app; Directory = directory; _temporary = temporary; }
        public static async Task<Server> Start(string? directory = null, bool readonlyBeta = false, bool faultAppend = false)
        {
            var temporary = directory is null;
            directory ??= Path.Combine(Path.GetTempPath(), "connected-test-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IClock>(new TimeProviderClock(TimeProvider.System));
            builder.Services.AddSingleton<IRedactor, CaptureGroupRedactor>();
            builder.Services.AddSingleton<IApplicationStorageProvider>(sp =>
            {
                var sqlite = new SqliteApplicationStorageProvider(sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRedactor>());
                return faultAppend ? new FaultProvider(sqlite) : sqlite;
            });
            var options = new ConnectedKernelOptions { Enabled = true, Environment = "development", StorageRoot = directory,
                Applications = [new() { Tenant = "example", Application = "alpha", Credential = AlphaKey, WriteNamespaces = ["Example.Connected"], EventPrefixes = ["example."] },
                    new() { Tenant = "example", Application = "beta", Credential = BetaKey, Writable = !readonlyBeta, WriteNamespaces = ["Example.Connected"], EventPrefixes = ["example."] }] };
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(sp => new ApplicationStorageFactory(options, sp.GetServices<IApplicationStorageProvider>()));
            builder.Services.AddSingleton(sp => new ConnectedControlState(options, sp.GetServices<IApplicationStorageProvider>()));
            var app = builder.Build();
            ConnectedKernelEndpoints.Map(app);
            await app.StartAsync();
            return new Server(app, directory, temporary);
        }
        public async Task<ConnectedKernelClient> Client(string key, ApplicationScope scope)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _clients.Add(http);
            var client = new ConnectedKernelClient(http, new Uri(Url), key, scope, allowInsecureDevelopment: true);
            await client.ConnectAsync();
            return client;
        }
        public ApplicationStores Stores(string key) => _app.Services.GetRequiredService<ApplicationStorageFactory>().Authenticate(key)!.Value.Stores;
        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            if (_temporary && System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class FaultProvider(IApplicationStorageProvider inner) : IApplicationStorageProvider
    {
        public string Id => inner.Id;
        public ApplicationStores Open(ApplicationStorageBinding binding)
        {
            var stores = inner.Open(binding);
            return binding.Scope.Application == "alpha" ? new FaultStores(stores) : stores;
        }
    }
    private sealed class FaultStores(ApplicationStores inner) : ApplicationStores
    {
        public override IResourceStore Resources => inner.Resources;
        public override IEventLog Events { get; } = new FaultEvents(inner.Events);
        public override IArtifactStore Artifacts => inner.Artifacts;
        public override ILeaseStore Leases => inner.Leases;
        public override IOutboxStore Outbox => inner.Outbox;
        public override IIdempotencyStore Idempotency => inner.Idempotency;
        public override IApprovalStore Approvals => inner.Approvals;
        public override IOperationTracker Operations => inner.Operations;
        public override Burbuja.Core.Abstractions.Store.IDecisionCache<JsonNode> Decisions => inner.Decisions;
        public override void Dispose() => inner.Dispose();
    }
    private sealed class FaultEvents(IEventLog inner) : IEventLog
    {
        public async ValueTask<long> AppendAsync(EventAppend evt, CancellationToken ct)
        {
            await inner.AppendAsync(evt, ct);
            throw new IOException("Injected failure after durable effect, before receipt settlement.");
        }
        public IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct) => inner.TailAsync(streamId, afterSeq, ct);
        public IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct) => inner.TailAllAsync(lanes, afterSeq, ct);
        public IAsyncEnumerable<LedgerEvent> ReadRangeAsync(string streamId, long fromSeq, long toSeqExclusive, int limit, RenderLane? lanes, CancellationToken ct) =>
            inner.ReadRangeAsync(streamId, fromSeq, toSeqExclusive, limit, lanes, ct);
    }
}
