using Burbuja.Plugins.Realtime.Redis;
using Burbuja.Realtime.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// Runs the full backplane contract against a real Redis/Valkey server.
/// Each test gets its own unique key prefix for isolation.
/// </summary>
[Collection("Redis")]
public sealed class RedisBackplaneContractTests : BackplaneContractTests, IAsyncLifetime
{
    private IConnectionMultiplexer? _mux;
    private string _prefix = "";

    protected override TimeSpan Ttl => TimeSpan.FromMilliseconds(450);

    protected override IRealtimeBackplane Backplane =>
        new RedisBackplane(_mux!, _prefix, ownsMultiplexer: false);

    protected override async Task ExpireAsync() =>
        await Task.Delay(Ttl + TimeSpan.FromMilliseconds(100), Ct);

    public async ValueTask InitializeAsync()
    {
        var mux = await RedisFixture.TryGetMultiplexerAsync();
        if (mux is null)
            Assert.Skip("No Redis server available: set BURBUJA_REDIS to a connection string or ensure Docker is running.");
        _mux = mux;
        // Unique per-test prefix with hash tag so all keys stay in one slot.
        _prefix = "{brt-test-" + Guid.NewGuid().ToString("N")[..8] + "}:";
    }

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null)
            await CleanupPrefixAsync(_mux, _prefix);
    }

    private static async Task CleanupPrefixAsync(IConnectionMultiplexer mux, string prefix)
    {
        try
        {
            var db = mux.GetDatabase();
            // All keys share the hash tag, so SCAN the slot via the pattern.
            var server = mux.GetServers().FirstOrDefault();
            if (server is null) return;
            var keys = server.Keys(pattern: prefix + "*").ToArray();
            if (keys.Length > 0)
                await db.KeyDeleteAsync(keys);
        }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Redis-specific tests: two-process isolation, missing configuration error,
/// and pruning of expired listing members.
/// </summary>
[Collection("Redis")]
public sealed class RedisBackplaneSpecificTests : IAsyncLifetime
{
    private IConnectionMultiplexer? _mux;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(350);

    public async ValueTask InitializeAsync()
    {
        var mux = await RedisFixture.TryGetMultiplexerAsync();
        if (mux is null)
            Assert.Skip("No Redis server available: set BURBUJA_REDIS to a connection string or ensure Docker is running.");
        _mux = mux;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RedisBackplane NewBackplane(string prefix) =>
        new(_mux!, prefix, ownsMultiplexer: false);

    /// <summary>A second backplane instance (simulating a second process) sees records and contests claims.</summary>
    [Fact]
    public async Task Two_backplane_instances_share_state_and_contend_for_claims()
    {
        var prefix = "{brt-2proc-" + Guid.NewGuid().ToString("N")[..8] + "}:";
        await using var bp1 = NewBackplane(prefix);
        await using var bp2 = NewBackplane(prefix);

        // Node published by bp1 is visible to bp2.
        Assert.True(await bp1.PublishNodeAsync(new BackplaneNode("n1", "inst1", "{}"), Long, Ct));
        var nodes = await bp2.ListNodesAsync(Ct);
        Assert.Single(nodes);
        Assert.Equal("n1", nodes[0].NodeId);

        // bp2 cannot steal the node owned by bp1.
        Assert.False(await bp2.PublishNodeAsync(new BackplaneNode("n1", "inst2", "{}"), Long, Ct));

        // Seat claimed by bp1 is not available to bp2.
        Assert.Equal("lobby1", await bp1.ClaimSeatAsync("player1", "lobby1", Long, Ct));
        Assert.Equal("lobby1", await bp2.ClaimSeatAsync("player1", "lobby2", Long, Ct));

        // bp2 can answer calls from bp1.
        await using var serve = await bp2.ServeAsync("n1", (req, _) => Task.FromResult("pong:" + req), Ct);
        var reply = await bp1.CallAsync("n1", "ping", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("pong:ping", reply);

        // Cleanup
        await bp1.RemoveNodeAsync("n1", "inst1", Ct);
        await bp1.ReleaseSeatAsync("player1", "lobby1", Ct);
    }

    /// <summary>Missing Realtime:Redis:Configuration throws with the key in the message.</summary>
    [Fact]
    public void Missing_configuration_key_throws_InvalidOperationException_naming_the_key()
    {
        var plugin = new RealtimeRedisPlugin();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        plugin.ConfigureServices(services);
        var sp = services.BuildServiceProvider();

        // The factory throws synchronously; avoid the Func<Task> overload by using an explicit Action.
        var ex = Assert.Throws<InvalidOperationException>((Action)(() =>
            sp.GetRequiredService<IRealtimeBackplane>()));
        Assert.Contains("Realtime:Redis:Configuration", ex.Message);
    }

    /// <summary>Expired members are pruned from the listing set on the next read.</summary>
    [Fact]
    public async Task Expired_listing_members_are_pruned_on_next_list()
    {
        var prefix = "{brt-prune-" + Guid.NewGuid().ToString("N")[..8] + "}:";
        await using var bp = NewBackplane(prefix);

        var lobby = new BackplaneLobby("l1", "n1", "g", true, null, [], "{}");
        await bp.PublishLobbiesAsync([lobby], Short, Short, Ct);

        // Listed now.
        Assert.Single(await bp.ListLobbiesAsync("g", Ct));

        // Wait for expiry.
        await Task.Delay(Short + TimeSpan.FromMilliseconds(100), Ct);

        // After expiry the list is empty and the dead member was removed from the set.
        Assert.Empty(await bp.ListLobbiesAsync("g", Ct));

        // The set itself is now empty (member was pruned).
        var db = _mux!.GetDatabase();
        var members = await db.SetMembersAsync(prefix + "listed:g");
        Assert.Empty(members);
    }
}

/// <summary>
/// Shared Redis connection: use BURBUJA_REDIS env var, or start a throwaway Valkey container.
/// All tests within the [Collection("Redis")] share one server start/stop per test run.
/// </summary>
[CollectionDefinition("Redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture> { }

public sealed class RedisFixture : IAsyncLifetime
{
    private string? _containerId;
    private static IConnectionMultiplexer? _sharedMux;

    /// <summary>The configuration string of the server <see cref="TryGetMultiplexerAsync"/> connected to, for
    /// hosts that open their own connection.</summary>
    public static string? ConnectionString { get; private set; }

    /// <summary>Returns a connected multiplexer, or null when Redis cannot be reached and Docker is unavailable.</summary>
    public static async Task<IConnectionMultiplexer?> TryGetMultiplexerAsync()
    {
        if (_sharedMux is not null)
            return _sharedMux;

        // 1. Env var.
        var envConnStr = Environment.GetEnvironmentVariable("BURBUJA_REDIS");
        if (!string.IsNullOrWhiteSpace(envConnStr))
        {
            try
            {
                var opts = ConfigurationOptions.Parse(envConnStr);
                opts.AbortOnConnectFail = true;
                opts.ConnectTimeout = 5000;
                _sharedMux = await ConnectionMultiplexer.ConnectAsync(opts);
                ConnectionString = envConnStr;
                await _sharedMux.GetDatabase().PingAsync();
                return _sharedMux;
            }
            catch { return null; }
        }

        // 2. Start a Valkey container via Docker CLI.
        try
        {
            var startResult = await RunDockerAsync("run -d --rm -p 127.0.0.1::6379 valkey/valkey:8-alpine");
            if (!startResult.Success) return null;
            var containerId = startResult.Output.Trim();

            // Read the mapped port.
            var portResult = await RunDockerAsync($"port {containerId} 6379/tcp");
            if (!portResult.Success) { await StopContainerAsync(containerId); return null; }

            var mappedPort = portResult.Output.Trim().Split(':').Last();
            if (!int.TryParse(mappedPort, out var port)) { await StopContainerAsync(containerId); return null; }

            // Store the container id for cleanup via the fixture.
            _containerIdStore = containerId;

            // Wait for Valkey to be ready.
            var connStr = $"127.0.0.1:{port}";
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    var opts = ConfigurationOptions.Parse(connStr);
                    opts.AbortOnConnectFail = true;
                    opts.ConnectTimeout = 500;
                    var mux = await ConnectionMultiplexer.ConnectAsync(opts);
                    await mux.GetDatabase().PingAsync();
                    _sharedMux = mux;
                    ConnectionString = connStr;
                    return _sharedMux;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }

            await StopContainerAsync(containerId);
            return null;
        }
        catch { return null; }
    }

    // Container id stored across the static lifetime so InitializeAsync can pick it up.
    private static string? _containerIdStore;

    public async ValueTask InitializeAsync()
    {
        await TryGetMultiplexerAsync();
        _containerId = _containerIdStore;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sharedMux is not null)
        {
            try { await _sharedMux.CloseAsync(); } catch { /* ignored */ }
            _sharedMux = null;
        }

        if (_containerId is not null)
            await StopContainerAsync(_containerId);
    }

    private static async Task StopContainerAsync(string id)
    {
        try { await RunDockerAsync($"stop {id}"); } catch { /* ignored */ }
    }

    private static async Task<(bool Success, string Output)> RunDockerAsync(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return (false, "");
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode == 0, output);
    }
}
