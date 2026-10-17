using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Burbuja.Realtime.Lobbies;
using Burbuja.Realtime.Matchmaking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>An in-memory transport: records every frame the lobby sends and how it was closed.</summary>
internal sealed class FakeConnection : IPlayerConnection
{
    private readonly List<JsonObject> _frames = [];

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public RealtimeCloseCode? ClosedWith { get; private set; }
    public string? CloseReason { get; private set; }
    public IConnectionInbox Inbox { get; set; } = null!;

    public IReadOnlyList<JsonObject> Frames
    {
        get
        {
            lock (_frames)
                return _frames.ToList();
        }
    }

    public IEnumerable<JsonObject> OfType(string type) => Frames.Where(f => (string?)f["t"] == type);

    public bool TrySend(ReadOnlyMemory<byte> utf8Frame)
    {
        if (ClosedWith is not null)
            return false;
        lock (_frames)
            _frames.Add(JsonNode.Parse(utf8Frame.Span)!.AsObject());
        return true;
    }

    public void Close(RealtimeCloseCode code, string reason)
    {
        ClosedWith ??= code;
        CloseReason ??= reason;
    }

    public void Send(string json) => Inbox.Receive(Encoding.UTF8.GetBytes(json));
}

/// <summary>A game whose session records every callback and obeys a few test commands.</summary>
internal sealed class RecordingGame(GameRules? rules = null, string id = "test") : IRealtimeGame
{
    public string Id => id;
    public string DisplayName => "Test";
    public GameRules Rules { get; } = rules ?? new GameRules
    {
        MinPlayers = 2,
        MaxPlayers = 4,
        TickRate = 20,
        StartCountdown = TimeSpan.FromSeconds(3),
        ReconnectGrace = TimeSpan.FromSeconds(10),
        ResultsLinger = TimeSpan.FromSeconds(5),
        MaxInboundMessagesPerSecond = 10,
    };

    public List<RecordingSession> Sessions { get; } = [];

    public IGameSession CreateSession(GameSessionOptions options)
    {
        var session = new RecordingSession();
        Sessions.Add(session);
        return session;
    }
}

internal sealed class RecordingSession : GameSession, IDisposable
{
    public List<string> Calls { get; } = [];
    public int Ticks { get; private set; }
    public int Messages { get; private set; }
    public bool Disposed { get; private set; }

    public override void OnPlayerJoined(ISessionContext context, RealtimePlayer player) => Calls.Add($"joined:{player.PlayerId}:{player.Slot}");

    public override void OnPlayerConnectionChanged(ISessionContext context, RealtimePlayer player, bool connected) =>
        Calls.Add($"{(connected ? "reconnected" : "dropped")}:{player.PlayerId}");

    public override void OnPlayerLeft(ISessionContext context, RealtimePlayer player, LeaveReason reason) =>
        Calls.Add($"left:{player.PlayerId}:{reason}");

    public override void OnMatchStarted(ISessionContext context) => Calls.Add("started");

    public override void OnMatchEnded(ISessionContext context, MatchResult result) => Calls.Add($"ended:{result.Reason}");

    public override void OnTick(ISessionContext context)
    {
        Ticks++;
        context.Broadcast("state", new { tick = context.Tick });
    }

    public override void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message)
    {
        Messages++;
        switch (message.Type)
        {
            case "end":
                context.EndMatch(new MatchResult("won", [player.PlayerId], new JsonObject { ["by"] = player.PlayerId }));
                break;
            case "boom":
                throw new InvalidOperationException("game bug");
            case "kick":
                context.Kick(message.Data.GetString()!, "test");
                break;
            case "say":
                context.Broadcast("said", new { from = player.PlayerId, text = message.Data.GetString() }, player.PlayerId);
                break;
        }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A lobby manager and queue with no loops: the test owns the clock and every step.</summary>
internal sealed class Harness
{
    public Harness(RecordingGame? game = null, RealtimeOptions? options = null, params LobbyServer[] servers)
    {
        Game = game ?? new RecordingGame();
        Options = options ?? new RealtimeOptions { Enabled = true };
        if (servers.Length == 0)
            servers = [Server("s1", maxLobbies: 4)];
        Manager = new LobbyManager(new RealtimeGames([Game]), new LobbyBalancer(servers, new LeastLoadedPlacement(), true),
            Options, Time, NullLoggerFactory.Instance, runLoops: false);
        Queue = new MatchmakingQueue(Manager, Microsoft.Extensions.Options.Options.Create(Options), Time,
            NullLogger<MatchmakingQueue>.Instance);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    public RecordingGame Game { get; }
    public RealtimeOptions Options { get; }
    public LobbyManager Manager { get; }
    public MatchmakingQueue Queue { get; }

    public LobbyServer Server(string id, int maxLobbies, string region = "local") =>
        new(id, region, maxLobbies, null, Time, NullLogger.Instance);

    public static PlayerIdentity Player(string id, int? skill = null) => new(id, id.ToUpperInvariant(),
        skill is null ? null : new Dictionary<string, string> { [PlayerAttributes.Skill] = skill.Value.ToString() });

    public Lobby CreateLobby(LobbyVisibility visibility = LobbyVisibility.Public, LobbyOrigin origin = LobbyOrigin.Hosted)
    {
        var created = Manager.Create(new CreateLobbyRequest(Game.Id, visibility, origin));
        return created.Lobby ?? throw new InvalidOperationException(created.Error);
    }

    public FakeConnection Connect(Lobby lobby, string playerId)
    {
        var reservation = Manager.Reserve(lobby, Player(playerId)).Reservation
            ?? throw new InvalidOperationException("no reservation");
        return Admit(reservation);
    }

    public FakeConnection Admit(SeatReservation reservation)
    {
        var connection = new FakeConnection();
        var admission = Manager.Admit(reservation.Token, connection);
        Assert.True(admission.Accepted, admission.Error);
        connection.Inbox = admission.Inbox!;
        return connection;
    }

    /// <summary>Advances the clock in steps no longer than a tick, stepping every lobby each time.</summary>
    public void Run(TimeSpan duration, TimeSpan? step = null)
    {
        var interval = step ?? TimeSpan.FromMilliseconds(50);
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += interval)
        {
            Time.Advance(interval);
            Manager.StepAll(Time.GetUtcNow());
        }
    }

    public void Step() => Manager.StepAll(Time.GetUtcNow());
}
