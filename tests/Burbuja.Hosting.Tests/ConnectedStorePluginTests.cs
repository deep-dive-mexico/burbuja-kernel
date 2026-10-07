using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Applications;
using Burbuja.Core.Clock;
using Burbuja.Core.Redaction;
using Burbuja.Core.Sdk.Applications;
using Burbuja.Hosting.Applications;
using Burbuja.Plugins.Store.Connected;
using Burbuja.Plugins.Store.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Burbuja.Hosting.Tests;

/// <summary>
/// A host composed with the Store.Connected plugin serves every durable store from a separate kernel
/// service: the same contracts, no database file, explicit unavailability when the kernel is gone.
/// </summary>
public sealed class ConnectedStorePluginTests
{
    private const string Key = "connected-store-plugin-credential-0123456789abcdef";
    private const string HostKey = "connected-store-embedded-host-credential-0123456789abcdef";

    [Fact]
    public void Plugin_requires_a_complete_registration_and_opens_nothing_locally()
    {
        var missing = Host(new Dictionary<string, string?> { ["ConnectedStore:Url"] = "https://kernel.invalid" });
        var error = Assert.Throws<InvalidOperationException>(() => missing.GetRequiredService<IResourceStore>());
        Assert.Contains("Credential", error.Message);
        Assert.Contains("Tenant", error.Message);
        var manifest = new StoreConnectedPlugin().Manifest;
        Assert.Equal(["store.connected"], manifest.Provides);
        Assert.DoesNotContain(Core.Abstractions.Plugin.PluginCapability.FilesystemWrite, manifest.Requires);
        Assert.Contains(Core.Abstractions.Plugin.PluginCapability.Network, manifest.Requires);
        Assert.True(manifest.LoadOrder > new StoreSqlitePlugin().Manifest.LoadOrder, "remote stores must replace a local store plugin installed beside them");
    }

    [Fact]
    public async Task Host_stores_round_trip_through_the_kernel_binding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kernel = await Kernel.Start();
        using var host = Host(Settings(kernel.Url));
        var resources = host.GetRequiredService<IResourceStore>();
        var id = "/subscriptions/local/resourceGroups/dev/providers/Example.Connected/notes/first";
        var created = await resources.CreateAsync(Envelope(id, "first", "one"), ct);
        Assert.Equal(id.ToLowerInvariant(), created.Id.ToLowerInvariant());
        await Assert.ThrowsAsync<Burbuja.Core.Arm.ArmException>(async () => await resources.CreateAsync(Envelope(id, "first", "again"), ct));
        var seen = await kernel.Stores.Resources.GetAsync(created.Id, ct);
        Assert.Equal("one", seen!.Properties["text"]!.GetValue<string>());

        var events = host.GetRequiredService<IEventLog>();
        var seq = await events.AppendAsync(new EventAppend("example/stream", "first", "example.note.created", "created",
            new JsonObject { ["id"] = id }, RenderLane.Log, null, null, "Example.Connected"), ct);
        var read = new List<LedgerEvent>();
        await foreach (var evt in events.ReadRangeAsync("example/stream", 0, seq + 1, 10, null, ct)) read.Add(evt);
        Assert.Single(read);
        Assert.Equal("example.note.created", read[0].Type);

        var leases = host.GetRequiredService<ILeaseStore>();
        var lease = await leases.AcquireAsync("notes/first", "host-a", TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(lease);
        Assert.Null(await leases.AcquireAsync("notes/first", "host-b", TimeSpan.FromMinutes(1), ct));
        await leases.ReleaseAsync("notes/first", "host-a", lease!.FencingToken, ct);

        var outbox = host.GetRequiredService<IOutbox>();
        await outbox.EnqueueAsync(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), "example.notify", new JsonObject { ["id"] = id }), ct);
        var claimed = await host.GetRequiredService<IOutboxStore>().ClaimAsync(5, DateTimeOffset.UtcNow.AddSeconds(1), ct);
        Assert.Single(claimed);

        var artifacts = host.GetRequiredService<IArtifactStore>();
        await artifacts.PutAsync("notes/first.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), "text/plain", null, ct);
        var blob = await artifacts.GetAsync("notes/first.txt", ct);
        Assert.NotNull(blob);
        using var reader = new StreamReader(blob!.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync(ct));

        var decisions = host.GetRequiredService<IDecisionCache<JsonNode>>();
        await decisions.SetAsync("example/stream", "k", JsonValue.Create("v")!, ct);
        Assert.Equal("v", (await decisions.GetAsync("example/stream", "k", ct))!.GetValue<string>());

        Assert.Same(resources, host.GetRequiredService<IOperationTracker>());
        Assert.Same(resources, host.GetRequiredService<IApprovalStore>());
        Assert.Same(resources, host.GetRequiredService<IIdempotencyStore>());
        var handshake = host.GetRequiredService<ConnectedStoreConnection>().Handshake;
        Assert.Equal(new ApplicationScope("development", "example", "alpha"), handshake!.Scope);
    }

    [Fact]
    public async Task An_unreachable_kernel_is_explicit_unavailability_not_a_silent_local_store()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kernel = await Kernel.Start();
        var url = kernel.Url;
        await kernel.DisposeAsync();
        using var host = Host(Settings(url));
        var error = Assert.Throws<InvalidOperationException>(() => host.GetRequiredService<IResourceStore>());
        Assert.Contains("cannot reach the kernel service", error.Message);
        Assert.True(error.InnerException is KernelUnavailableException or HttpRequestException, error.InnerException?.GetType().Name);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await host.GetRequiredService<IEventLog>().AppendAsync(new EventAppend("example/s", "n", "example.x", "x", null, RenderLane.Log, null, null, "Example.Connected"), ct));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task A_wrong_credential_or_scope_fails_the_host_before_any_store_use()
    {
        await using var kernel = await Kernel.Start();
        var settings = Settings(kernel.Url);
        settings["ConnectedStore:Application"] = "beta";
        using var host = Host(settings);
        Assert.ThrowsAny<Exception>(() => host.GetRequiredService<IResourceStore>());
    }

    [Fact]
    public async Task An_embedded_runtime_host_owns_its_whole_binding_including_kernel_namespaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kernel = await Kernel.Start();
        var settings = Settings(kernel.Url);
        settings["ConnectedStore:Credential"] = HostKey;
        settings["ConnectedStore:Application"] = "host";
        using var embedded = Host(settings);
        using var plain = Host(Settings(kernel.Url));
        var roleId = "/subscriptions/local/providers/Burbuja.Authorization/roleAssignments/ra-1";
        var assignment = new ResourceEnvelope<JsonObject>(roleId, "ra-1", "Burbuja.Authorization/roleAssignments", null,
            new Dictionary<string, string>(), new JsonObject { ["role"] = "Owner" }, ProvisioningState.Succeeded,
            new SystemData(null, "host", "Application", null, null, null), "");
        await embedded.GetRequiredService<IResourceStore>().UpsertAsync(assignment, null, ct);
        Assert.NotNull(await kernel.StoresFor(HostKey).Resources.GetAsync(roleId.ToLowerInvariant(), ct));
        await embedded.GetRequiredService<IEventLog>().AppendAsync(new EventAppend("arm/requests", "r", "arm.request", "x", null, RenderLane.Log, null, null, "Burbuja.Core"), ct);
        var approvals = embedded.GetRequiredService<IApprovalStore>();
        var request = new ApprovalRequest("stream/1", null, ApprovalKind.Permission, "tool", "t-1", "decide", null, "Gray", "run", null, "because", ["yes", "no"], false, TimeSpan.FromMinutes(5));
        approvals.Put(new ApprovalRecord("ap-1", request, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, null));
        Assert.NotNull(kernel.StoresFor(HostKey).Approvals.Get("ap-1"));
        Assert.Contains("embedded-runtime-binding", embedded.GetRequiredService<ConnectedStoreConnection>().Handshake!.Capabilities);

        var error = await Assert.ThrowsAsync<Burbuja.Core.Arm.ArmException>(async () =>
            await plain.GetRequiredService<IResourceStore>().UpsertAsync(assignment, null, ct));
        Assert.Equal(403, error.Error.HttpStatus);
        Assert.Null(await kernel.StoresFor(Key).Resources.GetAsync(roleId.ToLowerInvariant(), ct));
        Assert.DoesNotContain("embedded-runtime-binding", plain.GetRequiredService<ConnectedStoreConnection>().Handshake!.Capabilities);
    }

    [Fact]
    public void An_embedded_runtime_registration_cannot_also_use_the_scoped_runtime()
    {
        var options = new ConnectedKernelOptions { Enabled = true, Environment = "development", StorageRoot = Path.GetTempPath(),
            Applications = [new() { Tenant = "t", Application = "a", Credential = Key, Provider = "memory", EmbeddedRuntime = true, RuntimeEnabled = true }] };
        var error = Assert.Throws<InvalidOperationException>(() => new ApplicationStorageFactory(options,
            [new InMemoryApplicationStorageProvider(new TimeProviderClock(TimeProvider.System), new CaptureGroupRedactor())]));
        Assert.Contains("embedded-runtime", error.Message);
        options.Applications[0].RuntimeEnabled = false;
        using var factory = new ApplicationStorageFactory(options, [new InMemoryApplicationStorageProvider(new TimeProviderClock(TimeProvider.System), new CaptureGroupRedactor())]);
        Assert.True(factory.Authenticate(Key)!.Value.Embedded);
    }

    private static Dictionary<string, string?> Settings(string url) => new()
    {
        ["ConnectedStore:Url"] = url,
        ["ConnectedStore:Credential"] = Key,
        ["ConnectedStore:Environment"] = "development",
        ["ConnectedStore:Tenant"] = "example",
        ["ConnectedStore:Application"] = "alpha",
        ["ConnectedStore:Actor"] = "host-a",
        ["ConnectedStore:AllowInsecureDevelopment"] = "true",
    };

    private static ServiceProvider Host(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddSingleton<IClock>(new TimeProviderClock(TimeProvider.System));
        services.AddSingleton<IRedactor, CaptureGroupRedactor>();
        new StoreConnectedPlugin().ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    private static ResourceEnvelope<JsonObject> Envelope(string id, string name, string text) => new(id, name, "Example.Connected/notes", null,
        new Dictionary<string, string>(), new JsonObject { ["text"] = text }, ProvisioningState.Succeeded,
        new SystemData(null, "host-a", "Application", null, null, null), "");

    private sealed class Kernel : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Directory { get; }
        public string Url => _app.Urls.Single();
        public ApplicationStores Stores => StoresFor(Key);
        public ApplicationStores StoresFor(string key) => _app.Services.GetRequiredService<ApplicationStorageFactory>().Authenticate(key)!.Value.Stores;
        private Kernel(WebApplication app, string directory) { _app = app; Directory = directory; }

        public static async Task<Kernel> Start()
        {
            var directory = Path.Combine(Path.GetTempPath(), "connected-store-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IClock>(new TimeProviderClock(TimeProvider.System));
            builder.Services.AddSingleton<IRedactor, CaptureGroupRedactor>();
            builder.Services.AddSingleton<IApplicationStorageProvider>(sp =>
                new SqliteApplicationStorageProvider(sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRedactor>()));
            var options = new ConnectedKernelOptions
            {
                Enabled = true, Environment = "development", StorageRoot = directory,
                Applications = [new() { Tenant = "example", Application = "alpha", Credential = Key,
                    WriteNamespaces = ["Example.Connected"], EventPrefixes = ["example."] },
                    new() { Tenant = "example", Application = "host", Credential = HostKey, EmbeddedRuntime = true }],
            };
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(sp => new ApplicationStorageFactory(options, sp.GetServices<IApplicationStorageProvider>()));
            builder.Services.AddSingleton(sp => new ConnectedControlState(options, sp.GetServices<IApplicationStorageProvider>()));
            builder.Services.AddSingleton<ScopedApplicationRuntime>();
            var app = builder.Build();
            ConnectedKernelEndpoints.Map(app);
            await app.StartAsync();
            return new Kernel(app, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
