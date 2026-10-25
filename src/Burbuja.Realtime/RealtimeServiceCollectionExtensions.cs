using Burbuja.Core.Options;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Burbuja.Realtime.Lobbies;
using Burbuja.Realtime.Matchmaking;
using Burbuja.Realtime.Players;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the realtime module. Inert until <c>Realtime:Enabled</c> is true; games come from every
    /// <see cref="IRealtimeGame"/> service, typically contributed by plugins. A host may add its own
    /// <see cref="IPlayerAuthenticator"/> or <see cref="ILobbyPlacementStrategy"/> services alongside, and a
    /// plugin may replace the in-process <see cref="IRealtimeBackplane"/> with a shared one to form a fleet.
    /// </summary>
    public static IServiceCollection AddBurbujaRealtime(this IServiceCollection services)
    {
        services.AddOptions<RealtimeOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(RealtimeOptions.Section).Bind(o));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => RealtimeNode.Create(NodeId(sp)));
        services.TryAddSingleton<IRealtimeBackplane>(sp => new LocalBackplane(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<RealtimeGames>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILobbyPlacementStrategy, LeastLoadedPlacement>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILobbyPlacementStrategy, PackedPlacement>());
        services.TryAddSingleton(CreateBalancer);
        services.TryAddSingleton<LobbyManager>();
        services.TryAddSingleton<RealtimeFleet>();
        services.TryAddSingleton<IRealtimeGateway>(sp => sp.GetRequiredService<LobbyManager>());
        services.TryAddSingleton<MatchmakingQueue>();
        services.TryAddSingleton<GuestTokens>();
        services.TryAddSingleton<ApiKeyPlayerAuthenticator>();
        if (!services.Any(d => d.ServiceType == typeof(IPlayerAuthenticator) && d.ImplementationFactory?.Method.DeclaringType == typeof(RealtimeServiceCollectionExtensions)))
        {
            services.AddSingleton<IPlayerAuthenticator>(sp => sp.GetRequiredService<ApiKeyPlayerAuthenticator>());
            services.AddSingleton<IPlayerAuthenticator, GuestPlayerAuthenticator>();
        }

        services.TryAddSingleton<PlayerAuthentication>();
        services.AddHostedService<RealtimeHostedService>();
        return services;
    }

    /// <summary>The configured lobby servers behind the configured strategy; every mistake is reported at once.</summary>
    internal static LobbyBalancer CreateBalancer(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<RealtimeOptions>>().Value;
        var time = sp.GetRequiredService<TimeProvider>();
        var logs = sp.GetRequiredService<ILoggerFactory>();
        var configured = options.Servers.Count > 0
            ? options.Servers
            : [new LobbyServerOptions { Id = DefaultServerId(sp) }];

        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var games = sp.GetRequiredService<RealtimeGames>();
        foreach (var server in configured)
        {
            if (string.IsNullOrWhiteSpace(server.Id) || !ids.Add(server.Id))
                errors.Add($"server '{server.Id}': ids must be present and unique");
            if (server.MaxLobbies < 1)
                errors.Add($"server '{server.Id}': MaxLobbies must be at least 1");
            if (server.AllowedGames is { } allowed && (allowed.Length == 0 ||
                allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Length || allowed.Any(id => games.Find(id) is null)))
                errors.Add($"server '{server.Id}': AllowedGames must contain unique registered game IDs and cannot be empty");
            if (string.IsNullOrWhiteSpace(server.Region))
                errors.Add($"server '{server.Id}': Region is required");
            if (server.PublicUrl is { Length: > 0 } url
                && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss" or "http" or "https")))
                errors.Add($"server '{server.Id}': PublicUrl must be an absolute ws(s) or http(s) URL");
        }

        var strategies = sp.GetServices<ILobbyPlacementStrategy>().ToList();
        var strategy = strategies.LastOrDefault(s => string.Equals(s.Name, options.Placement, StringComparison.OrdinalIgnoreCase));
        if (strategy is null)
            errors.Add($"placement '{options.Placement}' is unknown; registered: {string.Join(", ", strategies.Select(s => s.Name))}");
        if (errors.Count > 0)
            throw new InvalidOperationException("Realtime configuration is invalid: " + string.Join("; ", errors));

        var servers = configured
            .Select(s => new LobbyServer(s.Id, s.Region, s.MaxLobbies, s.PublicUrl, time, logs.CreateLogger<LobbyServer>(), s.AllowedGames))
            .ToList();
        return new LobbyBalancer(servers, strategy!, options.AllowCrossRegionPlacement);
    }

    private static string DefaultServerId(IServiceProvider sp)
    {
        var node = sp.GetService<IOptions<KernelOptions>>()?.Value.NodeId;
        return string.IsNullOrWhiteSpace(node) ? "local" : node;
    }

    private static string NodeId(IServiceProvider sp)
    {
        var node = sp.GetService<IOptions<KernelOptions>>()?.Value.NodeId;
        return string.IsNullOrWhiteSpace(node) ? KernelOptions.FallbackNodeId() : node;
    }
}

/// <summary>Boot validation, joining the fleet, the heartbeat and matchmaking loops, and an orderly end of
/// every lobby on shutdown.</summary>
internal sealed class RealtimeHostedService(
    IServiceProvider services,
    IOptions<RealtimeOptions> options,
    TimeProvider time,
    ILogger<RealtimeHostedService> log) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return;

        // Resolving these validates every game and the fleet; a mistake stops the host here, not at the
        // first player's request.
        var fleet = services.GetRequiredService<RealtimeFleet>();
        await fleet.StartAsync(cancellationToken);
        await services.GetRequiredService<MatchmakingQueue>().StartAsync(cancellationToken);
        var manager = fleet.Local;
        log.LogInformation("Realtime enabled on node {NodeId}: games [{Games}], servers [{Servers}], placement {Placement}, backplane {Backplane}.",
            fleet.NodeId,
            string.Join(", ", manager.Games.All.Select(g => g.Id)),
            string.Join(", ", manager.Balancer.Servers.Select(s => $"{s.Id}@{s.Region}x{s.MaxLobbies}")),
            manager.Balancer.Strategy.Name,
            fleet.Backplane.GetType().Name);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return Task.CompletedTask;
        var fleet = services.GetRequiredService<RealtimeFleet>();
        var queue = services.GetRequiredService<MatchmakingQueue>();
        return Task.WhenAll(
            RepeatAsync(fleet.HeartbeatInterval, fleet.HeartbeatAsync, "Fleet heartbeat", stoppingToken),
            RepeatAsync(queue.Interval, ct => queue.RunCycleAsync(time.GetUtcNow(), ct), "Matchmaking cycle", stoppingToken));
    }

    private async Task RepeatAsync(TimeSpan interval, Func<CancellationToken, Task> work, string name, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await work(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "{Work} failed.", name);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!options.Value.Enabled)
            return;
        // Each step runs whatever the one before it threw: players must get their close frames and the node must
        // leave the fleet even when the backplane is failing.
        var fleet = services.GetRequiredService<RealtimeFleet>();
        await StepAsync("Stopping matchmaking", () => services.GetRequiredService<MatchmakingQueue>().DisposeAsync().AsTask());
        await StepAsync("Ending this node's lobbies", fleet.Local.ShutdownAsync);
        await StepAsync("Leaving the fleet", () => fleet.DisposeAsync().AsTask());
    }

    private async Task StepAsync(string name, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Step} failed during shutdown.", name);
        }
    }
}
