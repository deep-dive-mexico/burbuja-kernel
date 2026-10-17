using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Transport;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// What a game brings and what an action game needs from the kernel: its own datatypes (typed JSON, numerics
/// vectors, its own serializer options, its own binary encoding), input acknowledgement for client prediction,
/// and an honest account of the tick budget.
/// </summary>
public sealed class GameDataAndNetcodeTests
{
    private static readonly GameRules ThreeD = new()
    {
        MinPlayers = 1,
        MaxPlayers = 4,
        TickRate = 20,
        StartCountdown = TimeSpan.Zero,
        ReconnectGrace = TimeSpan.FromSeconds(10),
        MaxInboundMessagesPerSecond = 100,
    };

    [Fact]
    public void Numerics_vectors_travel_as_named_components_not_as_empty_objects()
    {
        // The pitfall the kernel's options exist for: Vector3's components are fields, so plain web options
        // write nothing at all.
        Assert.Equal("{}", JsonSerializer.Serialize(new Vector3(1, 2, 3), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var pose = new Pose(new Vector3(1.5f, -2, 3), new Vector2(0.25f, 1), Quaternion.Identity);
        var json = JsonSerializer.Serialize(pose, RealtimeJson.Default);
        Assert.Equal("""{"position":{"x":1.5,"y":-2,"z":3},"velocity":{"x":0.25,"y":1},"rotation":{"x":0,"y":0,"z":0,"w":1}}""", json);
        Assert.Equal(pose, JsonSerializer.Deserialize<Pose>(json, RealtimeJson.Default));
        Assert.Equal(new Vector3(4, 5, 0), JsonSerializer.Deserialize<Vector3>("""{"X":4,"y":5,"extra":true}""", RealtimeJson.Default));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Vector3>("[1,2,3]", RealtimeJson.Default));
    }

    [Fact]
    public void A_3d_session_reads_typed_input_acknowledges_it_and_broadcasts_typed_state()
    {
        var game = new RecordingGame(ThreeD, "orbit", () => new OrbitSession());
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        h.Step();
        Assert.Equal(LobbyState.Running, lobby.State);

        var welcome = Assert.Single(a.OfType(RealtimeProtocol.Welcome))["d"]!;
        Assert.Equal(50, (double)welcome["tickIntervalMs"]!);
        Assert.Equal(0, (long)welcome["tick"]!);

        a.Send("""{"t":"thrust","d":{"direction":{"x":0,"y":1,"z":0}},"s":7}""");
        a.Send("""{"t":"thrust","d":{"direction":"up"},"s":8}""");
        h.Run(TimeSpan.FromMilliseconds(100));

        var state = a.OfType("state").Last();
        var me = state["d"]!["ships"]![0]!;
        Assert.Equal(8, (long)me["ack"]!);
        Assert.True((double)me["position"]!["y"]! > 0, state.ToJsonString());
        Assert.Equal(0, (double)me["position"]!["x"]!);
        Assert.Equal(1, (double)me["rotation"]!["w"]!);

        // A reloaded client restarts its own counter: the acknowledgement restarts with the connection.
        a.Inbox.Disconnected();
        h.Step();
        var again = h.Connect(lobby, "a");
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Null(again.OfType("state").Last()["d"]!["ships"]![0]!["ack"]);
    }

    [Fact]
    public void Binary_frames_carry_the_games_own_encoding_both_ways()
    {
        var game = new RecordingGame(ThreeD, "orbit", () => new OrbitSession());
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        var b = h.Connect(lobby, "b");
        h.Step();

        var thrust = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(thrust, 1);
        a.SendBinary("thrust", 42, thrust);
        h.Run(TimeSpan.FromMilliseconds(100));

        // The session answered with its packed snapshot: one binary frame per tick, encoded once, the tick in
        // the envelope.
        var frame = b.BinaryFrames.Last();
        Assert.True(RealtimeBinary.TryDecode(frame, out var type, out var tick, out var offset));
        Assert.Equal("pos", type);
        Assert.Equal(lobby.Tick, tick);
        var payload = frame.AsSpan(offset);
        Assert.Equal(2 * 16, payload.Length);
        Assert.True(BinaryPrimitives.ReadSingleLittleEndian(payload[4..]) > 0, "player a moved along x");
        Assert.Equal(42, (long)a.OfType("state").Last()["d"]!["ships"]![0]!["ack"]!);

        // Kernel types never travel binary, and a truncated envelope is refused like a bad JSON frame.
        a.SendBinary("sys.leave", 1, []);
        a.SendRawBinary([5, (byte)'s', (byte)'h']);
        h.Step();
        Assert.Equal(2, a.OfType(RealtimeProtocol.Error).Count(e => (string?)e["d"]!["code"] == "bad_frame"));
        Assert.True(lobby.HasSeat("a"));
        Assert.Throws<ArgumentException>(() => RealtimeBinary.Encode("ñ", 0, []));
        Assert.Throws<ArgumentException>(() => RealtimeBinary.Encode("hit fx", 0, []));
        Assert.False(RealtimeBinary.TryDecode([1, (byte)' ', 0, 0, 0, 0, 0, 0, 0, 0], out _, out _, out _));
    }

    [Fact]
    public void The_acknowledgement_only_moves_forward()
    {
        var game = new RecordingGame(ThreeD, "orbit", () => new OrbitSession());
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        h.Step();

        long? Ack()
        {
            h.Run(TimeSpan.FromMilliseconds(50));
            return (long?)a.OfType("state").Last()["d"]!["ships"]![0]!["ack"];
        }

        a.Send("""{"t":"thrust","d":{"direction":{"x":1,"y":0,"z":0}}}""");
        Assert.Null(Ack());
        a.Send("""{"t":"thrust","d":{"direction":{"x":1,"y":0,"z":0}},"s":5}""");
        Assert.Equal(5, Ack());

        // An unnumbered JSON message, a binary one numbered 0 and a late lower number all leave it at 5.
        a.Send("""{"t":"thrust","d":{"direction":{"x":0,"y":1,"z":0}}}""");
        a.SendBinary("emote", 0, [1]);
        a.Send("""{"t":"thrust","d":{"direction":{"x":0,"y":1,"z":0}},"s":3}""");
        Assert.Equal(5, Ack());
        a.SendBinary("thrust", 6, new byte[12]);
        Assert.Equal(6, Ack());
    }

    [Fact]
    public void A_closed_lobby_keeps_its_counters_and_reports_no_load()
    {
        var game = new RecordingGame(ThreeD, "orbit", () => new OrbitSession());
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        h.Step();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Step();
        lobby.RequestClose("test");
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        h.Step();

        Assert.Equal(LobbyState.Closed, lobby.State);
        Assert.Equal(15, lobby.Performance.DroppedTicks);
        Assert.Equal(0, lobby.Performance.Busy);
    }

    [Fact]
    public void A_game_brings_its_own_serializer_options_and_the_kernel_keeps_its_own()
    {
        var options = RealtimeJson.CreateOptions();
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        var game = new RecordingGame(ThreeD, "orbit", () => new OrbitSession()) { SerializerOptions = options };
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        h.Step();
        a.Send("""{"t":"thrust","d":{"direction":{"x":1,"y":0,"z":0}},"s":3}""");
        h.Run(TimeSpan.FromMilliseconds(100));

        Assert.NotNull(a.OfType("state").Last()["d"]!["ships"]![0]!["player_id"]);
        Assert.NotNull(Assert.Single(a.OfType(RealtimeProtocol.Welcome))["d"]!["playerId"]);
    }

    [Fact]
    public void A_lobby_accounts_for_every_tick_it_drops_and_publishes_its_load()
    {
        var game = new RecordingGame(ThreeD, "orbit", () => new OrbitSession());
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        h.Step();
        Assert.Equal(LobbyState.Running, lobby.State);

        // A one-second stall at 20 Hz: five ticks catch up, the other fifteen are dropped, not replayed late.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Step();
        Assert.Equal(5, lobby.Tick);
        var performance = lobby.Performance;
        Assert.Equal(15, performance.DroppedTicks);
        Assert.Equal(5, performance.TicksInWindow);
        Assert.True(performance.Busy >= 0);
        Assert.Equal(performance, lobby.Snapshot().Performance);
        Assert.Equal(5, lobby.Snapshot().Tick);
        Assert.Equal(performance.Busy, h.Manager.Server(lobby.ServerId)!.Status().Load);
    }

    [Fact]
    public void A_tick_over_its_budget_is_counted_as_an_overrun()
    {
        var rules = ThreeD with { TickRate = 100 };
        var game = new RecordingGame(rules, "slow", () => new SlowSession(TimeSpan.FromMilliseconds(25)));
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        h.Step();

        // Exactly one window: it holds the slow first tick, and every later tick fit its 10 ms budget.
        h.Run(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        var window = lobby.Performance;
        Assert.Equal(1, window.Overruns);
        Assert.True(window.MaxTickMilliseconds >= 20, $"max tick {window.MaxTickMilliseconds} ms");

        // The count is cumulative; the next window's timings no longer carry the slow tick.
        h.Run(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, lobby.Performance.Overruns);
    }

    [Fact]
    public async Task Placement_reads_never_wait_on_a_running_tick()
    {
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        var game = new RecordingGame(ThreeD, "block", () => new BlockingSession(entered, gate));
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        h.Step();
        h.Time.Advance(TimeSpan.FromMilliseconds(50));

        var stepping = Task.Run(h.Step, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            // The tick holds the lobby's lock; the balancer's view and the seat count do not need it.
            var status = await Task.Run(() => h.Manager.Server(lobby.ServerId)!.Status(), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, status.Players);
            Assert.Equal(1, lobby.SeatCount);
        }
        finally
        {
            gate.Set();
        }

        await stepping;
    }

    [Fact]
    public void A_close_reason_is_cut_to_the_frame_limit_without_splitting_a_character()
    {
        Assert.Equal("kicked", WebSocketPlayerConnection.CloseReason("kicked"));
        var reason = WebSocketPlayerConnection.CloseReason(new string('é', 100));
        Assert.Equal(61, reason.Length);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(reason) <= 123);
        var emoji = WebSocketPlayerConnection.CloseReason(string.Concat(Enumerable.Repeat("🎮", 40)));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(emoji) <= 123);
        Assert.False(char.IsHighSurrogate(emoji[^1]));
    }

    private sealed record Pose(Vector3 Position, Vector2 Velocity, Quaternion Rotation);

    private sealed record ThrustInput(Vector3 Direction);

    /// <summary>A tiny 3D session: ships drift along their last thrust, every tick a typed JSON snapshot with
    /// each player's acknowledged input and a packed binary position frame.</summary>
    private sealed class OrbitSession : GameSession
    {
        private readonly Dictionary<string, (Vector3 Position, Vector3 Thrust)> _ships = new(StringComparer.Ordinal);

        public override void OnPlayerJoined(ISessionContext context, RealtimePlayer player) =>
            _ships[player.PlayerId] = (Vector3.Zero, Vector3.Zero);

        public override void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message)
        {
            if (message.Type != "thrust" || !_ships.TryGetValue(player.PlayerId, out var ship))
                return;
            Vector3 direction;
            if (message.Format == FrameFormat.Binary)
            {
                var span = message.Binary.Span;
                if (span.Length < 12)
                    return;
                direction = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(span),
                    BinaryPrimitives.ReadSingleLittleEndian(span[4..]), BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
            }
            else if (message.TryRead<ThrustInput>(out var input))
            {
                direction = input.Direction;
            }
            else
            {
                return;
            }

            _ships[player.PlayerId] = ship with { Thrust = direction.LengthSquared() > 1 ? Vector3.Normalize(direction) : direction };
        }

        public override void OnTick(ISessionContext context)
        {
            var dt = (float)context.TickDelta.TotalSeconds;
            foreach (var (id, ship) in _ships.ToList())
                _ships[id] = ship with { Position = ship.Position + ship.Thrust * 10 * dt };

            context.Broadcast("state", new
            {
                ships = context.Players.Select(p => new
                {
                    PlayerId = p.PlayerId,
                    Position = _ships[p.PlayerId].Position,
                    Rotation = Quaternion.Identity,
                    Ack = context.LastSequence(p.PlayerId),
                }),
            });

            // The packed form of the same state: per ship, u32 slot then x, y, z as f32.
            var packed = new byte[context.Players.Count * 16];
            for (var i = 0; i < context.Players.Count; i++)
            {
                var player = context.Players[i];
                var span = packed.AsSpan(i * 16);
                var position = _ships[player.PlayerId].Position;
                BinaryPrimitives.WriteInt32LittleEndian(span, player.Slot);
                BinaryPrimitives.WriteSingleLittleEndian(span[4..], position.X);
                BinaryPrimitives.WriteSingleLittleEndian(span[8..], position.Y);
                BinaryPrimitives.WriteSingleLittleEndian(span[12..], position.Z);
            }

            context.BroadcastBinary("pos", packed);
        }
    }

    private sealed class SlowSession(TimeSpan once) : GameSession
    {
        private bool _slept;

        public override void OnTick(ISessionContext context)
        {
            if (_slept)
                return;
            _slept = true;
            Thread.Sleep(once);
        }
    }

    private sealed class BlockingSession(ManualResetEventSlim entered, ManualResetEventSlim gate) : GameSession
    {
        public override void OnTick(ISessionContext context)
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
