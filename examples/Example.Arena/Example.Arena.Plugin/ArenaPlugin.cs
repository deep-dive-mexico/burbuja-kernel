using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Realtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Example.Arena.Plugin;

/// <summary>Contributes one realtime game. The kernel's realtime module does the rest: lobbies, queue,
/// placement and the WebSocket transport.</summary>
public sealed class ArenaPlugin : IBurbujaPlugin
{
    public const string Namespace = "Example.Arena";

    public PluginManifest Manifest { get; } = new(
        Namespace, "Example Arena real-time shooter", "1.0.0", PluginContracts.Version,
        "Example.Arena.Plugin.dll", typeof(ArenaPlugin).FullName!, PluginLoadKind.Dynamic,
        [Namespace], [], [], ["all"], null, false, [], 900);

    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IRealtimeGame, ArenaGame>();
}

public sealed class ArenaGame : IRealtimeGame
{
    public string Id => "arena";

    public string DisplayName => "Arena";

    public GameRules Rules { get; } = new()
    {
        MinPlayers = 2,
        MaxPlayers = 8,
        TickRate = 30,
        StartCountdown = TimeSpan.FromSeconds(3),
        MaxMatchDuration = TimeSpan.FromSeconds(ArenaSession.RoundSeconds + 30),
        AllowJoinInProgress = true,
        ReconnectGrace = TimeSpan.FromSeconds(10),
        ResultsLinger = TimeSpan.FromSeconds(8),
        MaxInboundMessagesPerSecond = 90,
        MaxMessageBytes = 512,
    };

    public IGameSession CreateSession(GameSessionOptions options) => new ArenaSession();
}
