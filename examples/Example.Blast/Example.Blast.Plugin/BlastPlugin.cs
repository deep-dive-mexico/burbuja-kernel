using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Realtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Example.Blast.Plugin;

/// <summary>Contributes one realtime game. The kernel's realtime module discovers every IRealtimeGame
/// registration and handles lobbies, queue, placement and the WebSocket transport.</summary>
public sealed class BlastPlugin : IBurbujaPlugin
{
    public const string Namespace = "Example.Blast";

    public PluginManifest Manifest { get; } = new(
        Namespace, "Example Blast battle game", "1.0.0", PluginContracts.Version,
        "Example.Blast.Plugin.dll", typeof(BlastPlugin).FullName!, PluginLoadKind.Dynamic,
        [Namespace], [], [], ["all"], null, false, [], 900);

    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IRealtimeGame, BlastGame>();
}

/// <summary>
/// "Blast": a server-authoritative 3D battle game for 2–8 players inspired by Bomberman 64's Battle mode.
/// Last player standing wins a round; first to <c>wins</c> rounds wins the match.
/// </summary>
public sealed class BlastGame : IRealtimeGame
{
    public string Id => "blast";

    public string DisplayName => "Blast";

    public GameRules Rules { get; } = new()
    {
        MinPlayers = 2,
        MaxPlayers = 8,
        TickRate = 30,
        StartCountdown = TimeSpan.FromSeconds(3),
        MaxMatchDuration = TimeSpan.FromMinutes(20),
        AllowJoinInProgress = true,
        ReconnectGrace = TimeSpan.FromSeconds(15),
        ResultsLinger = TimeSpan.FromSeconds(8),
        MaxInboundMessagesPerSecond = 90,
        MaxMessageBytes = 512,
    };

    public IGameSession CreateSession(GameSessionOptions options) => new BlastSession(options);
}
