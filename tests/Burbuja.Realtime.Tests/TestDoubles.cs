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
    private readonly List<byte[]> _binary = [];

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

    /// <summary>Binary frames as sent, envelope included.</summary>
    public IReadOnlyList<byte[]> BinaryFrames
    {
        get
        {
            lock (_frames)
                return _binary.ToList();
        }
    }

    public IEnumerable<JsonObject> OfType(string type) => Frames.Where(f => (string?)f["t"] == type);

    public bool TrySend(ReadOnlyMemory<byte> frame, FrameFormat format)
    {
        if (ClosedWith is not null)
            return false;
        lock (_frames)
        {
            if (format == FrameFormat.Binary)
                _binary.Add(frame.ToArray());
            else
                _frames.Add(JsonNode.Parse(frame.Span)!.AsObject());
        }

        return true;
    }

    public void Close(RealtimeCloseCode code, string reason)
    {
        ClosedWith ??= code;
        CloseReason ??= reason;
    }

    public void Send(string json) => Inbox.Receive(Encoding.UTF8.GetBytes(json), FrameFormat.Text);

    public void SendBinary(string type, long sequence, byte[] payload) =>
        Inbox.Receive(RealtimeBinary.Encode(type, sequence, payload), FrameFormat.Binary);

    public void SendRawBinary(byte[] frame) => Inbox.Receive(frame, FrameFormat.Binary);
}

/// <summary>A game whose session records every callback and obeys a few test commands.</summary>
internal sealed class RecordingGame(GameRules? rules = null, string id = "test", Func<IGameSession>? session = null) : IRealtimeGame
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

    public System.Text.Json.JsonSerializerOptions? SerializerOptions { get; init; }

    public IMatchmakingStrategy? Matchmaking { get; init; }

    /// <summary>What every session answers for <see cref="IGameSession.MinPlayersToStart"/>.</summary>
    public int? MinPlayersToStart { get; init; }

    public IGameSession CreateSession(GameSessionOptions options)
    {
        if (session is not null)
            return session();
        var recording = new RecordingSession { StartWith = MinPlayersToStart };
        Sessions.Add(recording);
        return recording;
    }
}

internal sealed class RecordingSession : GameSession, IDisposable
{
    public List<string> Calls { get; } = [];
    public int Ticks { get; private set; }
    public int Messages { get; private set; }
    public bool Disposed { get; private set; }
    public int? StartWith { get; init; }

    public override int? MinPlayersToStart => StartWith;

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

/// <summary>
/// One fleet node with no loops: a lobby manager, its fleet and queue over an in-process backplane, where the test
/// owns the clock and every step. <see cref="Join"/> adds another node of the same fleet (same clock, same
/// backplane), so a multi-node fleet runs in one process.
/// </summary>
internal sealed class Harness
{
    public Harness(RecordingGame? game = null, RealtimeOptions? options = null, params LobbyServer[] servers)
        : this(game, options, null, null, "node-1", servers)
    {
    }

    private Harness(RecordingGame? game, RealtimeOptions? options, FakeTimeProvider? time, LocalBackplane? backplane, string nodeId,
        LobbyServer[] servers)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        Backplane = backplane ?? new LocalBackplane(Time);
        Game = game ?? new RecordingGame();
        Options = options ?? new RealtimeOptions { Enabled = true };
        if (servers.Length == 0)
            servers = [Server(nodeId == "node-1" ? "s1" : nodeId + "-s1", maxLobbies: 4)];
        Node = RealtimeNode.Create(nodeId);
        Manager = new LobbyManager(new RealtimeGames([Game]), new LobbyBalancer(servers, new LeastLoadedPlacement(), true),
            Options, nodeId, Time, NullLoggerFactory.Instance, runLoops: false);
        Fleet = new RealtimeFleet(Manager, Backplane, Node, Options, Time, NullLogger.Instance);
        Queue = new MatchmakingQueue(Fleet, Options, Node.InstanceId, Time, NullLogger.Instance);
        Sync(Fleet.StartAsync(TestContext.Current.CancellationToken));
        Sync(Queue.StartAsync(TestContext.Current.CancellationToken));
    }

    public FakeTimeProvider Time { get; }
    public LocalBackplane Backplane { get; }
    public RealtimeNode Node { get; }
    public RecordingGame Game { get; }
    public RealtimeOptions Options { get; }
    public LobbyManager Manager { get; }
    public RealtimeFleet Fleet { get; }
    public MatchmakingQueue Queue { get; }

    /// <summary>Another node of this fleet, with its own game instance and servers.</summary>
    public Harness Join(string nodeId, RecordingGame? game = null, RealtimeOptions? options = null, params LobbyServer[] servers) =>
        new(game ?? new RecordingGame(Game.Rules, Game.Id), options ?? Options, Time, Backplane, nodeId, servers);

    public LobbyServer Server(string id, int maxLobbies, string region = "local") =>
        new(id, region, maxLobbies, null, Time, NullLogger.Instance);

    public static PlayerIdentity Player(string id, int? skill = null) => new(id, id.ToUpperInvariant(),
        skill is null ? null : new Dictionary<string, string> { [PlayerAttributes.Skill] = skill.Value.ToString() });

    /// <summary>A lobby placed by the fleet (it may land on another node), as this node's object when it is here.</summary>
    public Lobby CreateLobby(LobbyVisibility visibility = LobbyVisibility.Public, LobbyOrigin origin = LobbyOrigin.Hosted)
    {
        var created = Sync(Fleet.CreateAsync(new CreateLobbyRequest(Game.Id, visibility, origin), [], TestContext.Current.CancellationToken));
        return Manager.Find(created.Lobby?.LobbyId) ?? throw new InvalidOperationException(created.Error ?? "placed on another node");
    }

    public ReservationResult Reserve(Lobby lobby, string playerId) => Reserve(lobby.Id, playerId);

    public ReservationResult Reserve(string lobbyId, string playerId) =>
        Sync(Fleet.ReserveAsync(lobbyId, Player(playerId), TestContext.Current.CancellationToken));

    public FakeConnection Connect(Lobby lobby, string playerId)
    {
        var reservation = Reserve(lobby, playerId).Reservation
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

    /// <summary>Completes a call on the in-process backplane, which answers synchronously.</summary>
    public static T Sync<T>(Task<T> task)
    {
        Assert.True(task.IsCompleted, "an in-process fleet call did not complete synchronously");
        return task.GetAwaiter().GetResult();
    }

    public static void Sync(Task task)
    {
        Assert.True(task.IsCompleted, "an in-process fleet call did not complete synchronously");
        task.GetAwaiter().GetResult();
    }
}
