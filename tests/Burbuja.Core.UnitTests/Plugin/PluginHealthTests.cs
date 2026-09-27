using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Plugin;

/// <summary>
/// Track 3a: per-plugin health and the supervised hosted services. The kernel wraps exactly the
/// IHostedService descriptors a plugin registered, fails the boot when one throws (unless the plugin
/// opted into FailSoft), records a faulted BackgroundService, and folds every contributor into the
/// plugin's health check.
/// </summary>
public sealed class PluginHealthTests
{
    // ---- 5. only the plugin's own hosted services are wrapped --------------------------------

    [Fact]
    public void Only_the_plugins_own_hosted_services_are_wrapped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HostOwnedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<HostOwnedService>());

        var host = Boot(
            services,
            new PluginHealthRegistry(),
            settings: null,
            new TestPlugin("Alpha", s =>
            {
                s.AddSingleton<AlphaService>();
                s.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AlphaService>());
            }),
            new TestPlugin("Beta", s => s.AddSingleton<IHostedService, BetaService>()));

        // Registered after Boot, the way Program.cs adds the seed/heartbeat/worker services.
        services.AddSingleton<IHostedService, LateHostOwnedService>();

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.Count.ShouldBe(4);

        // The Host's own hosted service, registered before Boot, is never touched.
        hosted[0].ShouldBeOfType<HostOwnedService>();

        var alpha = hosted[1].ShouldBeOfType<SupervisedHostedService>();
        alpha.PluginId.ShouldBe("Alpha");
        alpha.Inner.ShouldBeSameAs(provider.GetRequiredService<AlphaService>());

        var beta = hosted[2].ShouldBeOfType<SupervisedHostedService>();
        beta.PluginId.ShouldBe("Beta");

        // ...nor is one registered after Boot, the way Program.cs adds its seed/heartbeat/worker services.
        hosted[3].ShouldBeOfType<LateHostOwnedService>();

        var snapshot = host.Health.Snapshot().Plugins;
        snapshot.Single(p => p.Id == "Alpha").HostedServices.ShouldBe([typeof(AlphaService).FullName!]);
        snapshot.Single(p => p.Id == "Beta").HostedServices.ShouldBe([typeof(BetaService).FullName!]);
    }

    // ---- 3. fail-fast rethrows, FailSoft records ---------------------------------------------

    [Fact]
    public async Task A_failing_start_fails_the_host_by_default()
    {
        var registry = new PluginHealthRegistry();
        var services = new ServiceCollection();
        Boot(services, registry, settings: null, new TestPlugin("Alpha", s => s.AddSingleton<IHostedService, ThrowingService>()));

        using var provider = services.BuildServiceProvider();
        var supervised = provider.GetServices<IHostedService>().OfType<SupervisedHostedService>().Single();
        supervised.FailSoft.ShouldBeFalse("fail-fast is the default: launchd/compose are the supervisors");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => supervised.StartAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("start exploded");

        var entry = registry.Snapshot().Plugins.Single();
        entry.State.ShouldBe(PluginHealthState.StartFailed);
        entry.Error.ShouldNotBeNull();
        entry.Error!.ShouldContain("start exploded");
        entry.Status.ShouldBe(PluginHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task FailSoft_records_the_failure_without_rethrowing()
    {
        var registry = new PluginHealthRegistry();
        var services = new ServiceCollection();
        Boot(
            services,
            registry,
            new Dictionary<string, string?> { ["Plugins:Alpha:FailSoft"] = "true" },
            new TestPlugin("Alpha", s => s.AddSingleton<IHostedService, ThrowingService>()));

        using var provider = services.BuildServiceProvider();
        var supervised = provider.GetServices<IHostedService>().OfType<SupervisedHostedService>().Single();
        supervised.FailSoft.ShouldBeTrue();

        // No throw: the host continues without that one service.
        await supervised.StartAsync(TestContext.Current.CancellationToken);

        var entry = registry.Snapshot().Plugins.Single();
        entry.State.ShouldBe(PluginHealthState.StartFailed);
        entry.Error.ShouldNotBeNull();
        entry.Error!.ShouldContain("start exploded");
        entry.Description.ShouldNotBeNull();
        entry.Description!.ShouldContain("failed to start");
        registry.StateHealth("Alpha").Status.ShouldBe(PluginHealthStatus.Unhealthy);
    }

    // ---- 4. a faulted BackgroundService marks the plugin Faulted -----------------------------

    [Fact]
    public async Task A_faulted_background_service_marks_the_plugin_faulted()
    {
        var registry = new PluginHealthRegistry();
        var background = new ExplodingBackgroundService();
        var services = new ServiceCollection();
        Boot(services, registry, settings: null, new TestPlugin("Alpha", s => s.AddSingleton<IHostedService>(background)));

        using var provider = services.BuildServiceProvider();
        var supervised = provider.GetServices<IHostedService>().OfType<SupervisedHostedService>().Single();

        // ExecuteAsync is still running: the start succeeds and the plugin is Started.
        await supervised.StartAsync(TestContext.Current.CancellationToken);
        registry.Snapshot().Plugins.Single().State.ShouldBe(PluginHealthState.Started);

        background.Explode();
        await WaitForAsync(() => registry.Snapshot().Plugins.Single().State == PluginHealthState.Faulted);

        var entry = registry.Snapshot().Plugins.Single();
        entry.Error.ShouldNotBeNull();
        entry.Error!.ShouldContain("background exploded");
        entry.Status.ShouldBe(PluginHealthStatus.Unhealthy);
        entry.Description.ShouldNotBeNull();
        entry.Description!.ShouldContain("faulted");
    }

    [Fact]
    public async Task A_background_service_that_faults_immediately_is_observed_and_marked_faulted()
    {
        // .NET 10's BackgroundService.StartAsync returns Task.CompletedTask even when ExecuteAsync
        // returned an already-faulted task: ExecuteTask is the only channel for the failure. The
        // supervisor observes it, so an immediate fault is recorded like a late one — never swallowed.
        var registry = new PluginHealthRegistry();
        var services = new ServiceCollection();
        Boot(services, registry, settings: null, new TestPlugin("Alpha", s => s.AddSingleton<IHostedService, SyncFaultingBackgroundService>()));

        using var provider = services.BuildServiceProvider();
        var supervised = provider.GetServices<IHostedService>().OfType<SupervisedHostedService>().Single();

        await supervised.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => registry.Snapshot().Plugins.Single().State == PluginHealthState.Faulted);

        var entry = registry.Snapshot().Plugins.Single();
        entry.Error.ShouldNotBeNull();
        entry.Error!.ShouldContain("background exploded immediately");
        entry.Status.ShouldBe(PluginHealthStatus.Unhealthy);
    }

    // ---- 4. the health check: registry state first, then every contributor, worst wins -------

    [Fact]
    public async Task The_health_check_folds_every_contributor_of_the_plugin_and_the_worst_wins()
    {
        var registry = new PluginHealthRegistry();
        registry.Register(TestManifest("Alpha"), [typeof(AlphaService).FullName!]);
        registry.RecordStarted("Alpha", typeof(AlphaService).FullName!);

        using var provider = new ServiceCollection()
            .AddSingleton<IPluginHealthContributor>(new FakeContributor(
                "Alpha",
                new PluginHealth(PluginHealthStatus.Degraded, "queue backing up", new JsonObject { ["depth"] = 7 })))
            .AddSingleton<IPluginHealthContributor>(new FakeContributor(
                "Beta",
                new PluginHealth(PluginHealthStatus.Unhealthy, "belongs to another plugin")))
            .BuildServiceProvider();

        var check = new PluginHealthCheck(registry, "Alpha", provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldBe("queue backing up");
        result.Data!["depth"].ShouldBe("7");

        var entry = registry.Snapshot().Plugins.Single();
        entry.Id.ShouldBe("Alpha");
        entry.Status.ShouldBe(PluginHealthStatus.Degraded);
        entry.Description.ShouldBe("queue backing up");
        entry.Data!["depth"]!.GetValue<int>().ShouldBe(7);

        // A contributor that belongs to another plugin is never folded in.
        entry.Status.ShouldNotBe(PluginHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task An_unhealthy_contributor_beats_a_healthy_registry_state()
    {
        var registry = new PluginHealthRegistry();
        registry.Register(TestManifest("Alpha"));

        using var provider = new ServiceCollection()
            .AddSingleton<IPluginHealthContributor>(new FakeContributor(
                "Alpha",
                new PluginHealth(PluginHealthStatus.Degraded, "slow")))
            .AddSingleton<IPluginHealthContributor>(new FakeContributor(
                "Alpha",
                new PluginHealth(PluginHealthStatus.Unhealthy, "store unreachable")))
            .BuildServiceProvider();

        var check = new PluginHealthCheck(registry, "Alpha", provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe("store unreachable");
        registry.StateHealth("Alpha").Status.ShouldBe(PluginHealthStatus.Healthy);
        registry.Snapshot().Plugins.Single().Status.ShouldBe(PluginHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task A_contributor_that_throws_is_unhealthy_for_its_own_plugin_only()
    {
        var registry = new PluginHealthRegistry();
        registry.Register(TestManifest("Alpha"));

        using var provider = new ServiceCollection()
            .AddSingleton<IPluginHealthContributor>(new FakeContributor("Alpha", new InvalidOperationException("boom")))
            .BuildServiceProvider();

        var check = new PluginHealthCheck(registry, "Alpha", provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description!.ShouldContain("boom");
    }

    [Fact]
    public void A_start_failure_is_unhealthy_and_a_plugin_with_no_services_is_healthy()
    {
        var registry = new PluginHealthRegistry();
        registry.Register(TestManifest("Alpha"), ["Alpha.Service"]);
        registry.Register(TestManifest("Beta"));
        registry.RecordStartFailed("Alpha", "Alpha.Service", new InvalidOperationException("no port"));

        var snapshot = registry.Snapshot();
        var alpha = snapshot.Plugins.Single(p => p.Id == "Alpha");
        alpha.Status.ShouldBe(PluginHealthStatus.Unhealthy);
        alpha.Error.ShouldNotBeNull();
        alpha.Error!.ShouldContain("no port");
        snapshot.Plugins.Single(p => p.Id == "Beta").Status.ShouldBe(PluginHealthStatus.Healthy);
        snapshot.Plugins.Single(p => p.Id == "Beta").HostedServices.ShouldBeEmpty();
        snapshot.Plugins.Single(p => p.Id == "Beta").State.ShouldBe(PluginHealthState.Registered);
        snapshot.Status.ShouldBe(PluginHealthStatus.Unhealthy, "the snapshot's overall status is the worst entry");
    }

    // ---- fixtures ----------------------------------------------------------------------------

    private static PluginHost Boot(
        ServiceCollection services,
        PluginHealthRegistry health,
        Dictionary<string, string?>? settings,
        params IBurbujaPlugin[] plugins)
    {
        var values = new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "control-plane",
            ["Kernel:ContractsVersion"] = PluginContracts.Version,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            values[key] = value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var kernel = new KernelOptions
        {
            Role = "control-plane",
            PluginsDirectory = "",
            // A plugin that registers a hosted service declares PluginCapability.BackgroundService,
            // and the kernel only grants that to a trusted plugin.
            TrustedPlugins = plugins.Select(p => p.Manifest.Id).ToArray(),
            ContractsVersion = PluginContracts.Version,
        };
        var host = new PluginHost(config, Microsoft.Extensions.Options.Options.Create(kernel), NullLogger<PluginHost>.Instance, null, health);
        host.Boot(services, plugins);
        return host;
    }

    private static PluginManifest TestManifest(string id) => new(
        id, id, "1.0.0", PluginContracts.Version, id + ".dll", "X.Y", PluginLoadKind.Static,
        [], [], [PluginCapability.BackgroundService], ["all"], null, true, [], 1);

    private sealed class TestPlugin(string id, Action<IServiceCollection> configure) : IBurbujaPlugin
    {
        public PluginManifest Manifest { get; } = TestManifest(id);

        public void ConfigureServices(IServiceCollection services) => configure(services);
    }

    private sealed class FakeContributor : IPluginHealthContributor
    {
        private readonly PluginHealth? _health;
        private readonly Exception? _error;

        public FakeContributor(string pluginId, PluginHealth health)
        {
            PluginId = pluginId;
            _health = health;
        }

        public FakeContributor(string pluginId, Exception error)
        {
            PluginId = pluginId;
            _error = error;
        }

        public string PluginId { get; }

        public ValueTask<PluginHealth> CheckAsync(CancellationToken ct) =>
            _error is null
                ? ValueTask.FromResult(_health!)
                : ValueTask.FromException<PluginHealth>(_error);
    }

    private sealed class HostOwnedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LateHostOwnedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlphaService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BetaService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("start exploded"));

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ExplodingBackgroundService : BackgroundService
    {
        private readonly TaskCompletionSource _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Explode() => _fault.SetException(new InvalidOperationException("background exploded"));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) => await _fault.Task;
    }

    private sealed class SyncFaultingBackgroundService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.FromException(new InvalidOperationException("background exploded immediately"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
                return;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("the supervisor never observed the fault");
    }
}
