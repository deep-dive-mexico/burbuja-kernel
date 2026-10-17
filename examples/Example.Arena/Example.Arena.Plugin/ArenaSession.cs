using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Burbuja.Realtime.Abstractions;

namespace Example.Arena.Plugin;

/// <summary>
/// A small server-authoritative top-down shooter. Clients send only intent (<c>input</c>: a move vector, an aim
/// angle and a trigger), as JSON or as a 13-byte binary frame; the server moves everyone, simulates bullets,
/// resolves hits against walls and players, and broadcasts a whole snapshot every tick, with each player's
/// last applied input sequence as <c>ack</c>. First to <see cref="KillsToWin"/> or the leader when the round
/// clock runs out wins.
/// </summary>
public sealed class ArenaSession : GameSession
{
    public const int Width = 1200;
    public const int Height = 720;
    public const int RoundSeconds = 120;
    public const int KillsToWin = 10;
    private const float PlayerRadius = 16;
    private const float PlayerSpeed = 240;
    private const float BulletSpeed = 680;
    private const float BulletLife = 1.1f;
    private const int Damage = 25;
    private const float FireCooldown = 0.18f;
    private const float RespawnDelay = 2f;
    private const float SpawnShield = 1f;

    /// <summary>Static cover: x, y, width, height.</summary>
    public static readonly (float X, float Y, float W, float H)[] Walls =
    [
        (280, 160, 40, 220), (880, 340, 40, 220), (480, 330, 240, 40),
        (140, 560, 200, 30), (860, 130, 200, 30), (580, 80, 40, 120), (580, 520, 40, 120),
    ];

    private static readonly (float X, float Y)[] Spawns =
    [
        (60, 60), (1140, 660), (1140, 60), (60, 660), (600, 30), (600, 690), (40, 360), (1160, 360),
    ];

    private readonly Dictionary<string, Fighter> _fighters = new(StringComparer.Ordinal);
    private readonly List<Bullet> _bullets = [];
    private float _clock;
    private bool _over;

    public override void OnPlayerJoined(ISessionContext context, RealtimePlayer player)
    {
        var fighter = new Fighter(player.PlayerId, player.Slot);
        _fighters[player.PlayerId] = fighter;
        Spawn(context, fighter);
        SendMap(context, player);
    }

    public override void OnPlayerLeft(ISessionContext context, RealtimePlayer player, LeaveReason reason)
    {
        _fighters.Remove(player.PlayerId);
        _bullets.RemoveAll(b => b.Owner == player.PlayerId);
    }

    public override void OnPlayerConnectionChanged(ISessionContext context, RealtimePlayer player, bool connected)
    {
        // A dropped player stands still rather than keep running on their last input.
        if (!connected && _fighters.TryGetValue(player.PlayerId, out var fighter))
            fighter.Input = default;
        if (connected)
            SendMap(context, player);
    }

    private static void SendMap(ISessionContext context, RealtimePlayer player) => context.Send(player.PlayerId, "map", new
    {
        width = Width,
        height = Height,
        roundSeconds = RoundSeconds,
        killsToWin = KillsToWin,
        playerRadius = PlayerRadius,
        walls = Walls.Select(w => new[] { w.X, w.Y, w.W, w.H }),
    });

    public override void OnMatchStarted(ISessionContext context)
    {
        _clock = 0;
        _bullets.Clear();
        foreach (var fighter in _fighters.Values)
        {
            fighter.Kills = 0;
            fighter.Deaths = 0;
            Spawn(context, fighter);
        }
    }

    public override void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message)
    {
        if (message.Type != "input" || !_fighters.TryGetValue(player.PlayerId, out var fighter))
            return;

        // The same intent in either encoding: JSON {x,y,aim,fire}, or binary f32 x, f32 y, f32 aim, u8 fire.
        Input input;
        if (message.Format == FrameFormat.Binary)
        {
            var bytes = message.Binary.Span;
            if (bytes.Length < InputBytes)
                return;
            input = new Input(BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]), bytes[12] != 0);
        }
        else if (!message.TryRead(out input))
        {
            return;
        }

        // Never trust the client beyond its intent: clamp and normalise everything it sends.
        var x = Finite(input.X);
        var y = Finite(input.Y);
        var length = MathF.Sqrt(x * x + y * y);
        if (length > 1)
        {
            x /= length;
            y /= length;
        }

        fighter.Input = new Input(x, y, float.IsFinite(input.Aim) ? Finite(input.Aim) : fighter.Input.Aim, input.Fire);
    }

    /// <summary>The binary input frame's payload size.</summary>
    public const int InputBytes = 13;

    public override void OnTick(ISessionContext context)
    {
        if (_over)
            return;
        var dt = (float)context.TickDelta.TotalSeconds;
        _clock += dt;

        foreach (var fighter in _fighters.Values)
        {
            fighter.Cooldown = MathF.Max(0, fighter.Cooldown - dt);
            fighter.Shield = MathF.Max(0, fighter.Shield - dt);
            fighter.Aim = fighter.Input.Aim;
            if (!fighter.Alive)
            {
                fighter.RespawnIn -= dt;
                if (fighter.RespawnIn <= 0)
                    Spawn(context, fighter);
                continue;
            }

            Move(fighter, fighter.Input.X * PlayerSpeed * dt, fighter.Input.Y * PlayerSpeed * dt);
            if (fighter.Input.Fire && fighter.Cooldown <= 0)
            {
                fighter.Cooldown = FireCooldown;
                var (dx, dy) = (MathF.Cos(fighter.Aim), MathF.Sin(fighter.Aim));
                _bullets.Add(new Bullet(fighter.Id, fighter.X + dx * (PlayerRadius + 4), fighter.Y + dy * (PlayerRadius + 4),
                    dx * BulletSpeed, dy * BulletSpeed, BulletLife));
            }
        }

        StepBullets(context, dt);
        Broadcast(context);

        var leader = _fighters.Values.OrderByDescending(f => f.Kills).FirstOrDefault();
        if (leader is { Kills: >= KillsToWin })
            Finish(context, "kill_limit");
        else if (_clock >= RoundSeconds)
            Finish(context, "time_up");
    }

    private void StepBullets(ISessionContext context, float dt)
    {
        for (var i = _bullets.Count - 1; i >= 0; i--)
        {
            var b = _bullets[i];
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;
            b.Life -= dt;
            if (b.Life <= 0 || b.X < 0 || b.Y < 0 || b.X > Width || b.Y > Height || Walls.Any(w => Inside(w, b.X, b.Y)))
            {
                _bullets.RemoveAt(i);
                continue;
            }

            var target = _fighters.Values.FirstOrDefault(f => f.Alive && f.Id != b.Owner && f.Shield <= 0
                && (f.X - b.X) * (f.X - b.X) + (f.Y - b.Y) * (f.Y - b.Y) <= PlayerRadius * PlayerRadius);
            if (target is null)
                continue;
            _bullets.RemoveAt(i);
            target.Hp -= Damage;
            if (target.Hp > 0)
                continue;

            target.Alive = false;
            target.Deaths++;
            target.RespawnIn = RespawnDelay;
            if (_fighters.TryGetValue(b.Owner, out var killer))
                killer.Kills++;
            context.Broadcast("kill", new { killer = b.Owner, victim = target.Id });
        }
    }

    private void Broadcast(ISessionContext context) => context.Broadcast("state", new
    {
        time = MathF.Max(0, RoundSeconds - _clock),
        players = _fighters.Values.Select(f => new
        {
            id = f.Id,
            slot = f.Slot,
            x = MathF.Round(f.X, 1),
            y = MathF.Round(f.Y, 1),
            aim = MathF.Round(f.Aim, 3),
            hp = f.Hp,
            alive = f.Alive,
            shield = f.Shield > 0,
            kills = f.Kills,
            deaths = f.Deaths,
            ack = context.LastSequence(f.Id),
        }),
        bullets = _bullets.Select(b => new[] { MathF.Round(b.X), MathF.Round(b.Y) }),
    });

    private void Finish(ISessionContext context, string reason)
    {
        _over = true;
        var best = _fighters.Values.Select(f => f.Kills).DefaultIfEmpty(0).Max();
        var winners = _fighters.Values.Where(f => f.Kills == best && best > 0).Select(f => f.Id).ToList();
        var board = new JsonArray(_fighters.Values.OrderByDescending(f => f.Kills).ThenBy(f => f.Deaths)
            .Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["kills"] = f.Kills, ["deaths"] = f.Deaths }).ToArray());
        context.EndMatch(new MatchResult(reason, winners, new JsonObject { ["scoreboard"] = board }));
    }

    private void Spawn(ISessionContext context, Fighter fighter)
    {
        // The spawn point farthest from every living opponent, with a little randomness to break ties.
        var spot = Spawns.OrderByDescending(s => _fighters.Values.Where(f => f.Alive && f != fighter)
                .Select(f => MathF.Sqrt((f.X - s.X) * (f.X - s.X) + (f.Y - s.Y) * (f.Y - s.Y)))
                .DefaultIfEmpty(10_000).Min() + context.Random.Next(0, 60))
            .First();
        fighter.X = spot.X;
        fighter.Y = spot.Y;
        fighter.Hp = 100;
        fighter.Alive = true;
        fighter.Shield = SpawnShield;
        fighter.Cooldown = 0;
    }

    private static void Move(Fighter fighter, float dx, float dy)
    {
        // Axis-separated so a player slides along a wall instead of sticking to it.
        var x = Math.Clamp(fighter.X + dx, PlayerRadius, Width - PlayerRadius);
        if (!Walls.Any(w => Overlaps(w, x, fighter.Y)))
            fighter.X = x;
        var y = Math.Clamp(fighter.Y + dy, PlayerRadius, Height - PlayerRadius);
        if (!Walls.Any(w => Overlaps(w, fighter.X, y)))
            fighter.Y = y;
    }

    private static bool Overlaps((float X, float Y, float W, float H) wall, float x, float y)
    {
        var nx = Math.Clamp(x, wall.X, wall.X + wall.W);
        var ny = Math.Clamp(y, wall.Y, wall.Y + wall.H);
        return (x - nx) * (x - nx) + (y - ny) * (y - ny) < PlayerRadius * PlayerRadius;
    }

    private static bool Inside((float X, float Y, float W, float H) wall, float x, float y) =>
        x >= wall.X && x <= wall.X + wall.W && y >= wall.Y && y <= wall.Y + wall.H;

    private static float Finite(float value) => float.IsFinite(value) ? Math.Clamp(value, -1000, 1000) : 0;

    /// <summary>The game's own input datatype, read straight from the JSON frame by the kernel.</summary>
    private readonly record struct Input(float X, float Y, float Aim, bool Fire);

    private sealed class Fighter(string id, int slot)
    {
        public string Id { get; } = id;
        public int Slot { get; } = slot;
        public float X { get; set; }
        public float Y { get; set; }
        public float Aim { get; set; }
        public int Hp { get; set; } = 100;
        public bool Alive { get; set; }
        public float Shield { get; set; }
        public float Cooldown { get; set; }
        public float RespawnIn { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public Input Input { get; set; }
    }

    private sealed class Bullet(string owner, float x, float y, float vx, float vy, float life)
    {
        public string Owner { get; } = owner;
        public float X { get; set; } = x;
        public float Y { get; set; } = y;
        public float Vx { get; } = vx;
        public float Vy { get; } = vy;
        public float Life { get; set; } = life;
    }
}
