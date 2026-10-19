using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Realtime.Abstractions;

namespace Example.Blast.Plugin;

/// <summary>
/// Server-authoritative Blast session. Runs at 30 Hz; broadcasts snapshots at 15 Hz (every other tick).
///
/// Round flow: Ready (2 s frozen) → Fight → optional Hurry (sudden death at 60 s left) → Over (3 s).
/// The first player to reach the configured <c>wins</c> count ends the match. The session ends the match
/// itself via <see cref="ISessionContext.EndMatch"/>; the kernel never hits the MaxMatchDuration for a
/// normal game.
///
/// Threading contract: plain fields, no locks, no blocking. An exception here ends the lobby.
/// </summary>
public sealed class BlastSession : GameSession
{
    // ─── settings (validated and clamped from lobby options) ─────────────────

    private readonly int _winsTarget;
    private readonly int _roundSeconds;
    private readonly bool _suddenDeath;
    private readonly bool _ghostsEnabled;
    private readonly int _botCount;

    /// <summary>CPU players fill the arena, so a lobby with bots starts with one human, as in the original.</summary>
    public override int? MinPlayersToStart => _botCount > 0 ? 1 : null;
    private readonly StageLayout _stage;

    // ─── round state ─────────────────────────────────────────────────────────

    private RoundPhase _phase;
    private int _roundNum;
    private float _phaseTimer;     // seconds until phase transition
    private float _roundTimer;     // time elapsed in current fight/hurry phase
    private int _closedLeft;       // hurry: columns closed from the left (x < _closedLeft = dead zone)
    private int _closedRight;      // hurry: columns closed from the right (x >= Width-_closedRight = dead zone)
    private float _hurryColTimer;  // seconds until next column closure

    // ─── entities ────────────────────────────────────────────────────────────

    private readonly Dictionary<string, BlastPlayer> _players = new(StringComparer.Ordinal);
    private readonly List<BlastBomb> _bombs = [];
    private readonly List<BlastHazard> _hazards = [];
    private readonly List<BlastItem> _items = [];
    private SoftBlockGrid _softBlocks = null!;
    private int _tickCount;

    // ─── match-end guard ─────────────────────────────────────────────────────

    private bool _matchEndScheduled;

    // Per-session ID counters — deterministic given the same sequence of events.
    private int _nextBombId;
    private int _nextHazardId;
    private int _nextItemId;

    private string NextBombId()   => $"b{++_nextBombId}";
    private string NextHazardId() => $"h{++_nextHazardId}";
    private string NextItemId()   => $"i{++_nextItemId}";

    public BlastSession(GameSessionOptions options)
    {
        _stage = BlastStages.Get(options.Settings.TryGetValue("stage", out var s) ? s : "rock-garden");

        _winsTarget = TryInt(options, "wins", 3, 1, 5);
        _roundSeconds = TryInt(options, "round-seconds", 120, 60, 300);
        _suddenDeath = TryBool(options, "sudden-death", true);
        _ghostsEnabled = TryBool(options, "ghosts", true);
        _botCount = Math.Clamp(TryInt(options, "bots", 0, 0, 7), 0, 7);
    }

    // ─── IGameSession callbacks ───────────────────────────────────────────────

    public override void OnPlayerJoined(ISessionContext context, RealtimePlayer player)
    {
        // Bots fill slots outside the kernel's human-seat table. A late human owns
        // their kernel slot and replaces its bot, keeping the eight-player limit.
        foreach (var bot in _players.Values.Where(p => p.IsBot && p.Slot == player.Slot).ToArray())
            OnPlayerLeft(context, new RealtimePlayer(bot.Id, bot.DisplayName, bot.Slot, new Dictionary<string, string>()), LeaveReason.Left);
        var p = new BlastPlayer
        {
            Id = player.PlayerId,
            Slot = player.Slot,
            DisplayName = player.DisplayName,
        };

        // A player who joins mid-match waits for the next round as a ghost spectator.
        if (context.State == LobbyState.Running)
        {
            p.Ghost = true;
            p.PendingRoundStart = true;
            p.Alive = false;
        }

        _players[player.PlayerId] = p;
        SendStage(context, player.PlayerId);
    }

    public override void OnPlayerLeft(ISessionContext context, RealtimePlayer player, LeaveReason reason)
    {
        if (_players.TryGetValue(player.PlayerId, out var p))
        {
            DropCarriedBombs(p);
            if (p.LatchedToId is not null)
                DetachGhost(p);
        }
        _players.Remove(player.PlayerId);
        _bombs.RemoveAll(b => b.OwnerId == player.PlayerId && b.State == BombState.Carried);
    }

    public override void OnPlayerConnectionChanged(ISessionContext context, RealtimePlayer player, bool connected)
    {
        if (!connected && _players.TryGetValue(player.PlayerId, out var p))
            p.Input = default; // stand still while disconnected
        if (connected)
            SendStage(context, player.PlayerId);
    }

    public override void OnMatchStarted(ISessionContext context)
    {
        // Add CPU bots into the player table.
        for (var i = 0; i < _botCount && _players.Count < 8; i++)
        {
            var slot = FindFreeSlot();
            var botId = $"bot-{i}";
            _players[botId] = new BlastPlayer
            {
                Id = botId, Slot = slot, IsBot = true,
                DisplayName = $"Bot {i + 1}",
            };
        }

        StartRound(context, 1);
    }

    public override void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message)
    {
        if (!_players.TryGetValue(player.PlayerId, out var p))
            return;

        if (message.Type == "stage")
        {
            SendStage(context, player.PlayerId);
            return;
        }

        if (message.Type == "input" && message.TryRead(out InputMsg? raw) && raw is not null)
        {
            // Clamp and normalise horizontal intent.
            var x = Finite(raw.X);
            var z = Finite(raw.Z);
            var len = MathF.Sqrt(x * x + z * z);
            if (len > 1f) { x /= len; z /= len; }
            p.Input = new InputState(x, z, raw.A, raw.B, raw.R);
        }
    }

    public override void OnTick(ISessionContext context)
    {
        if (_matchEndScheduled)
            return;

        _tickCount++;
        var dt = (float)context.TickDelta.TotalSeconds;

        switch (_phase)
        {
            case RoundPhase.Ready:
                _phaseTimer -= dt;
                if (_phaseTimer <= 0)
                    EnterFight(context);
                break;

            case RoundPhase.Fight:
            case RoundPhase.Hurry:
                SimulateRound(context, dt);
                break;

            case RoundPhase.Over:
                _phaseTimer -= dt;
                if (_phaseTimer <= 0)
                    AdvanceRound(context);
                break;
        }

        // Broadcast at 15 Hz (every other tick).
        if (_tickCount % 2 == 0)
            BroadcastState(context);
    }

    public override void OnMatchEnded(ISessionContext context, MatchResult result) { }

    // ─── round lifecycle ─────────────────────────────────────────────────────

    private void StartRound(ISessionContext context, int roundNum)
    {
        _roundNum = roundNum;
        _phase = RoundPhase.Ready;
        _phaseTimer = 2f;
        _roundTimer = 0;
        _closedLeft = 0;
        _closedRight = 0;
        _hurryColTimer = 0;

        _bombs.Clear();
        _hazards.Clear();
        _items.Clear();

        // Restore pending joiners and reset living players.
        foreach (var p in _players.Values)
        {
            p.PendingRoundStart = false;
            p.Ghost = false;
            p.Alive = true;
            p.StunTimer = 0;
            p.InvincibleTimer = 0;
            p.CarryingBombId = null;
            p.CarryingPlayerId = null;
            p.LatchedToId = null;
            p.LatchTimer = 0;
            p.LatchReverseCount = 0;
            p.ActiveBombs = 0;
            p.Input = default;
            p.PrevA = false;
            p.PrevB = false;
            p.PrevR = false;
        }

        // Assign spawn positions.
        var spawns = _stage.Spawns;
        var idx = 0;
        foreach (var p in _players.Values.OrderBy(p => p.Slot))
        {
            var sp = spawns[idx % spawns.Length];
            p.X = sp.X + 0.5f;
            p.Z = sp.Z + 0.5f;
            p.Tier = _stage.At(sp.X, sp.Z).Tier;
            p.FacingX = 0;
            p.FacingZ = 1;
            idx++;
        }

        // Generate soft blocks (deterministic from match seed + round number).
        PlaceSoftBlocks(context.Random);

        // Tell all connected players about the round reset via stage update.
        foreach (var player in context.Players)
            SendStage(context, player.PlayerId);
    }

    private void EnterFight(ISessionContext context)
    {
        _phase = RoundPhase.Fight;
        _roundTimer = 0;
    }

    private void SimulateRound(ISessionContext context, float dt)
    {
        _roundTimer += dt;
        var timeLeft = _roundSeconds - _roundTimer;

        // Trigger hurry.
        if (_suddenDeath && _phase == RoundPhase.Fight && timeLeft <= 60f)
        {
            _phase = RoundPhase.Hurry;
            _hurryColTimer = 0;
            // Remove all ghosts when hurry starts.
            foreach (var p in _players.Values)
                if (p.Ghost && !p.PendingRoundStart)
                {
                    p.Alive = false;
                    p.Ghost = false;
                    DropCarriedBombs(p);
                    DetachIfLatched(p);
                }
        }

        // Hurry: advance closing walls.
        if (_phase == RoundPhase.Hurry)
        {
            _hurryColTimer += dt;
            if (_hurryColTimer >= 1f)
            {
                _hurryColTimer -= 1f;
                var leftBound = _closedLeft;
                var rightBound = _stage.Width - 1 - _closedRight;
                if (leftBound < rightBound)
                {
                    _closedLeft++;
                    _closedRight++;
                    // Kill anything in the newly closed columns.
                    KillInClosedColumns(context);
                }
            }
        }

        // Time limit: draw.
        if (timeLeft <= 0)
        {
            EndRound(context, null, "time_up");
            return;
        }

        // Tick bots.
        foreach (var p in _players.Values)
            if (p.IsBot && p.Alive && !p.Ghost)
                BotThink(p, dt);

        // Process inputs and apply physics.
        ProcessInputs(context, dt);
        TickBombs(context, dt);
        TickHazards(dt);
        TickItems(context);

        // Check win condition.
        var living = _players.Values.Count(p => p.Alive && !p.Ghost);
        if (living <= 1)
        {
            var winner = _players.Values.FirstOrDefault(p => p.Alive && !p.Ghost);
            EndRound(context, winner?.Id, living == 0 ? "no_survivors" : "last_standing");
        }
    }

    private void EndRound(ISessionContext context, string? winnerId, string reason)
    {
        _phase = RoundPhase.Over;
        _phaseTimer = 3f;

        if (winnerId is not null && _players.TryGetValue(winnerId, out var winner))
            winner.Wins++;

        // Check match win.
        if (winnerId is not null && _players.TryGetValue(winnerId, out var w) && w.Wins >= _winsTarget)
        {
            _matchEndScheduled = true;
            var details = BuildScoreboard(reason);
            if (w.IsBot)
                context.EndMatch(MatchResult.Without("bot_won"));
            else
                context.EndMatch(new MatchResult("won", [winnerId], details));
        }
    }

    private void AdvanceRound(ISessionContext context)
    {
        if (_matchEndScheduled)
            return;
        StartRound(context, _roundNum + 1);
    }

    // ─── input processing ─────────────────────────────────────────────────────

    private void ProcessInputs(ISessionContext context, float dt)
    {
        // Read each victim's own intent before applying the ghost's controls. Do
        // this before the player loop so dictionary order cannot change shake-off.
        foreach (var ghost in _players.Values)
            if (ghost.Alive && ghost.Ghost && ghost.LatchedToId is { } victimId)
            {
                if (_players.TryGetValue(victimId, out var victim) && victim.Alive && !victim.Ghost)
                    TrackLatch(ghost, victim, dt);
                else
                    DetachGhost(ghost);
            }
        foreach (var p in _players.Values)
        {
            if (!p.Alive || p.PendingRoundStart)
                continue;

            // Determine effective input (may be overridden by a latching ghost).
            var possessor = _players.Values.FirstOrDefault(g => g.Alive && g.Ghost && g.LatchedToId == p.Id);
            var input = possessor is null ? p.Input : p.Input with { X = possessor.Input.X, Z = possessor.Input.Z };

            // Count down timers.
            if (p.StunTimer > 0) { p.StunTimer -= dt; continue; } // stunned: no action
            if (p.InvincibleTimer > 0) p.InvincibleTimer -= dt;

            // Edge detection for buttons.
            var aPress = input.A && !p.PrevA;
            var bPress = input.B && !p.PrevB;
            var bRelease = !input.B && p.PrevB;
            var rPress = input.R && !p.PrevR;
            p.PrevA = input.A;
            p.PrevB = input.B;
            p.PrevR = input.R;

            // Ghost-specific actions.
            if (p.Ghost)
            {
                MovePlayer(p, input, dt);
                // Ghost tries to latch onto a nearby living player.
                if (_ghostsEnabled && p.LatchedToId is null)
                    TryLatch(p);
                // Ghost interactions: kick / pick up bombs.
                if (bPress) TryGhostPickUp(context, p);
                else if (bRelease && p.CarryingBombId is not null) ThrowBomb(context, p, input);
                if (aPress && p.CarryingBombId is not null) PumpBomb(p);
                continue;
            }

            // Move.
            if (p.CarryingBombId is null && p.CarryingPlayerId is null)
                MovePlayer(p, input, dt);
            else
                MovePlayerCarrying(p, input, dt); // move with carried object

            // R: remote detonate.
            if (rPress && p.HasRemote)
                RemoteDetonate(context, p);

            // A: place bomb or pump.
            if (aPress)
            {
                if (p.CarryingBombId is not null)
                    PumpBomb(p);
                else
                    TryPlaceBomb(context, p);
            }

            // B: pick up / throw.
            if (bPress) TryPickUp(context, p);
            else if (bRelease && (p.CarryingBombId is not null || p.CarryingPlayerId is not null))
                ThrowCarried(context, p, input);
        }
    }

    // ─── movement ─────────────────────────────────────────────────────────────

    private const float PlayerSpeed = 4.5f;  // cells/sec
    private const float PlayerRadius = 0.4f;

    /// <summary>How high above its tier a player stands: a bomb coming down below this lands on their head.</summary>
    private const float HeadHeight = 1.2f;

    private void MovePlayer(BlastPlayer p, InputState input, float dt)
    {
        ApplyMoveDelta(p, input.X * PlayerSpeed * dt, input.Z * PlayerSpeed * dt);

        // Update facing for non-zero input.
        if (input.X * input.X + input.Z * input.Z > 0.01f)
        {
            p.FacingX = input.X;
            p.FacingZ = input.Z;
            // Normalise.
            var len = MathF.Sqrt(p.FacingX * p.FacingX + p.FacingZ * p.FacingZ);
            p.FacingX /= len;
            p.FacingZ /= len;
        }

        // Check kick: walk into a resting bomb.
        CheckKick(p, input);
    }

    private void MovePlayerCarrying(BlastPlayer p, InputState input, float dt)
    {
        ApplyMoveDelta(p, input.X * PlayerSpeed * dt, input.Z * PlayerSpeed * dt);
        if (input.X * input.X + input.Z * input.Z > 0.01f)
        {
            p.FacingX = input.X; p.FacingZ = input.Z;
            var len = MathF.Sqrt(p.FacingX * p.FacingX + p.FacingZ * p.FacingZ);
            p.FacingX /= len; p.FacingZ /= len;
        }
    }

    private void ApplyMoveDelta(BlastPlayer p, float dx, float dz)
    {
        // Axis-separated to allow sliding along walls.
        var nx = p.X + dx;
        if (!CollidesWithSolid(nx, p.Z, p.Tier, p))
            p.X = nx;
        var nz = p.Z + dz;
        if (!CollidesWithSolid(p.X, nz, p.Tier, p))
            p.Z = nz;

        // Clamp to prevent overshooting entirely past thin walls (rarely matters at 30 Hz).
        p.X = Math.Clamp(p.X, -0.5f, _stage.Width + 0.5f);
        p.Z = Math.Clamp(p.Z, -0.5f, _stage.Height + 0.5f);

        // Tier changes from ramp transitions.
        var cell = _stage.At((int)p.X, (int)p.Z);
        if (cell.Kind == CellKind.Ramp)
            p.Tier = RampTierAt(cell, p.X, p.Z);
        else if (cell.Kind == CellKind.Floor)
            p.Tier = cell.Tier;

        // Elimination: out-of-bounds or void/water cell.
        if (cell.IsElimination || p.X < 0 || p.X >= _stage.Width || p.Z < 0 || p.Z >= _stage.Height)
            Eliminate(null, p);
    }

    private bool CollidesWithSolid(float x, float z, int tier, BlastPlayer mover)
    {
        // Check a small set of cells the circle could touch.
        for (var cx = (int)(x - PlayerRadius); cx <= (int)(x + PlayerRadius); cx++)
            for (var cz = (int)(z - PlayerRadius); cz <= (int)(z + PlayerRadius); cz++)
            {
                var cell = _stage.At(cx, cz);
                if (cell.Kind == CellKind.Pillar) // hard wall
                {
                    if (CircleOverlapsCell(x, z, cx, cz))
                        return true;
                }
                if (_softBlocks.IsAlive(cx, cz))
                {
                    if (CircleOverlapsCell(x, z, cx, cz))
                        return true;
                }
            }

        // Resting/stationary bombs on the same tier block movement.
        foreach (var b in _bombs)
        {
            if (b.State != BombState.Resting) continue;
            if (b.Tier != tier) continue;
            if (b.OwnerId == mover.Id && b.FuseTimer >= BlastBomb.FuseSeconds - 0.2f) continue; // pass through own just-placed bomb briefly
            if ((b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z) < (PlayerRadius + BlastBomb.Radius) * (PlayerRadius + BlastBomb.Radius))
                return true;
        }
        return false;
    }

    private static bool CircleOverlapsCell(float x, float z, int cx, int cz)
    {
        var nx = Math.Clamp(x, cx, cx + 1f);
        var nz = Math.Clamp(z, cz, cz + 1f);
        var ddx = x - nx;
        var ddz = z - nz;
        return ddx * ddx + ddz * ddz < PlayerRadius * PlayerRadius;
    }

    private static int RampTierAt(CellDef cell, float x, float z)
    {
        // Progress along the ramp direction: 0 = low end, 1 = high end.
        float progress;
        if (cell.RampDX != 0)
            progress = cell.RampDX > 0 ? (x - (int)x) : (1f - (x - (int)x));
        else
            progress = cell.RampDZ > 0 ? (z - (int)z) : (1f - (z - (int)z));

        return progress >= 0.5f ? cell.RampToTier : cell.RampFromTier;
    }

    // ─── kicking ──────────────────────────────────────────────────────────────

    private void CheckKick(BlastPlayer p, InputState input)
    {
        if (input.X * input.X + input.Z * input.Z < 0.1f) return;

        foreach (var b in _bombs)
        {
            if (b.State != BombState.Resting || b.Tier != p.Tier) continue;
            var dx = b.X - p.X;
            var dz = b.Z - p.Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist > PlayerRadius + BlastBomb.Radius + 0.05f) continue;

            // Kick in the player's facing direction.
            var nx = MathF.Round(p.FacingX);
            var nz = MathF.Round(p.FacingZ);
            if (nx == 0 && nz == 0) { nx = (int)MathF.Round(input.X); nz = (int)MathF.Round(input.Z); }
            if (nx == 0 && nz == 0) continue;

            b.State = BombState.Rolling;
            b.VX = nx * BlastBomb.RollSpeed;
            b.VZ = nz * BlastBomb.RollSpeed;
            break; // one kick per tick
        }
    }

    // ─── bomb placement ───────────────────────────────────────────────────────

    private void TryPlaceBomb(ISessionContext context, BlastPlayer p)
    {
        if (p.ActiveBombs >= p.BombCapacity) return;

        var cx = (int)p.X;
        var cz = (int)p.Z;
        if (_stage.At(cx, cz).Kind is not CellKind.Floor and not CellKind.Ramp) return;
        if (_bombs.Any(b => b.CellX == cx && b.CellZ == cz && b.Tier == p.Tier && b.State == BombState.Resting)) return;

        var bomb = new BlastBomb
        {
            Id = NextBombId(),
            OwnerId = p.Id,
            X = cx + 0.5f, Z = cz + 0.5f,
            CellX = cx, CellZ = cz, Tier = p.Tier,
            FireLevel = p.FireLevel,
            IsRed = p.HasRed,
            IsRemote = p.HasRemote,
        };
        _bombs.Add(bomb);
        p.ActiveBombs++;
    }

    // ─── pick up / throw ──────────────────────────────────────────────────────

    private void TryPickUp(ISessionContext context, BlastPlayer p)
    {
        if (p.CarryingBombId is not null || p.CarryingPlayerId is not null) return;

        // Try a bomb first.
        var bomb = _bombs.Where(b => b.State == BombState.Resting && b.Tier == p.Tier)
            .OrderBy(b => Dist2(b.X, b.Z, p.X, p.Z))
            .FirstOrDefault(b => Dist2(b.X, b.Z, p.X, p.Z) <= (PlayerRadius + BlastBomb.Radius + 0.3f) * (PlayerRadius + BlastBomb.Radius + 0.3f));
        if (bomb is not null)
        {
            bomb.State = BombState.Carried;
            p.CarryingBombId = bomb.Id;
            return;
        }

        // Try a stunned player (ghosts cannot pick up players).
        if (!p.Ghost)
        {
            var victim = _players.Values
                .Where(v => v.Id != p.Id && v.Alive && !v.Ghost && v.StunTimer > 0 && v.Tier == p.Tier)
                .OrderBy(v => Dist2(v.X, v.Z, p.X, p.Z))
                .FirstOrDefault(v => Dist2(v.X, v.Z, p.X, p.Z) <= (PlayerRadius * 2 + 0.3f) * (PlayerRadius * 2 + 0.3f));
            if (victim is not null)
            {
                p.CarryingPlayerId = victim.Id;
                victim.StunTimer = float.MaxValue; // held until thrown
            }
        }
    }

    private void TryGhostPickUp(ISessionContext context, BlastPlayer p)
    {
        // Ghosts can pick up and throw bombs.
        TryPickUpBombOnly(p);
    }

    private void TryPickUpBombOnly(BlastPlayer p)
    {
        if (p.CarryingBombId is not null) return;
        var bomb = _bombs.Where(b => b.State == BombState.Resting && b.Tier == p.Tier)
            .OrderBy(b => Dist2(b.X, b.Z, p.X, p.Z))
            .FirstOrDefault(b => Dist2(b.X, b.Z, p.X, p.Z) <= (PlayerRadius + BlastBomb.Radius + 0.3f) * (PlayerRadius + BlastBomb.Radius + 0.3f));
        if (bomb is not null)
        {
            bomb.State = BombState.Carried;
            p.CarryingBombId = bomb.Id;
        }
    }

    private void ThrowCarried(ISessionContext context, BlastPlayer p, InputState input)
    {
        if (p.CarryingBombId is not null)
        {
            ThrowBomb(context, p, input);
        }
        else if (p.CarryingPlayerId is not null)
        {
            ThrowPlayer(context, p, input);
        }
    }

    private void ThrowBomb(ISessionContext context, BlastPlayer p, InputState input)
    {
        var bomb = _bombs.FirstOrDefault(b => b.Id == p.CarryingBombId);
        if (bomb is null) { p.CarryingBombId = null; return; }

        var stickMag = Math.Clamp(input.Length, 0.2f, 1f);
        var speed = BlastBomb.FlySpeed * stickMag;
        var angle = MathF.PI * 55f / 180f; // throw angle up from horizontal

        bomb.State = BombState.Flying;
        bomb.X = p.X;
        bomb.Z = p.Z;
        bomb.FlyVX = p.FacingX * speed;
        bomb.FlyVZ = p.FacingZ * speed;
        bomb.FlyVY = speed * MathF.Tan(angle) * 0.5f;
        bomb.FlyY = p.Tier + 0.6f; // launch slightly above tier
        bomb.Tier = p.Tier;

        p.CarryingBombId = null;
    }

    private void ThrowPlayer(ISessionContext context, BlastPlayer p, InputState input)
    {
        if (!_players.TryGetValue(p.CarryingPlayerId!, out var victim)) { p.CarryingPlayerId = null; return; }

        var stickMag = Math.Clamp(input.Length, 0.2f, 1f);
        var dist = 4f * stickMag;
        victim.StunTimer = 0;
        victim.X = p.X + p.FacingX * dist;
        victim.Z = p.Z + p.FacingZ * dist;
        victim.X = Math.Clamp(victim.X, 0, _stage.Width - 1);
        victim.Z = Math.Clamp(victim.Z, 0, _stage.Height - 1);

        // Check if thrown off the stage.
        var cell = _stage.At((int)victim.X, (int)victim.Z);
        if (cell.IsElimination || victim.X < 0 || victim.X >= _stage.Width)
            Eliminate(p.Id, victim);
        else
        {
            victim.StunTimer = 1.0f;
            victim.InvincibleTimer = 1.5f;
        }

        p.CarryingPlayerId = null;
    }

    // ─── pumping ──────────────────────────────────────────────────────────────

    private void PumpBomb(BlastPlayer p)
    {
        var bomb = _bombs.FirstOrDefault(b => b.Id == p.CarryingBombId);
        if (bomb is null) return;
        if (bomb.Pumps < 3) bomb.Pumps++;
    }

    // ─── remote detonation ────────────────────────────────────────────────────

    private void RemoteDetonate(ISessionContext context, BlastPlayer p)
    {
        var mine = _bombs.Where(b => b.OwnerId == p.Id && b.IsRemote && b.State == BombState.Resting).ToList();
        if (mine.Count == 0) return;
        // Detonate the oldest remote bomb.
        ExplodeBomb(context, mine[0]);
    }

    // ─── bomb simulation ──────────────────────────────────────────────────────

    private void TickBombs(ISessionContext context, float dt)
    {
        for (var i = _bombs.Count - 1; i >= 0; i--)
        {
            var b = _bombs[i];
            if (b.Exploded) { _bombs.RemoveAt(i); continue; }

            // Fuse countdown (not while carried or remote).
            if (b.State != BombState.Carried && !b.IsRemote)
            {
                b.FuseTimer -= dt;
                if (b.FuseTimer <= 0)
                {
                    ExplodeBomb(context, b);
                    continue;
                }
            }

            switch (b.State)
            {
                case BombState.Rolling: TickRollingBomb(context, b, dt); break;
                case BombState.Flying: TickFlyingBomb(context, b, dt); break;
                case BombState.Carried: UpdateCarriedBombPosition(b); break;
            }
        }
    }

    private void TickRollingBomb(ISessionContext context, BlastBomb b, float dt)
    {
        var nx = b.X + b.VX * dt;
        var nz = b.Z + b.VZ * dt;
        var cx = (int)nx;
        var cz = (int)nz;

        // Stop at a hard cell, soft block, or the grid edge.
        var cell = _stage.At(cx, cz);
        if (cell.Kind == CellKind.Pillar || _softBlocks.IsAlive(cx, cz) ||
            nx < 0 || nx >= _stage.Width || nz < 0 || nz >= _stage.Height)
        {
            b.State = BombState.Resting;
            b.VX = 0; b.VZ = 0;
            return;
        }

        // Check collision with a player (stun) or another resting bomb (billiard).
        foreach (var p in _players.Values)
        {
            if (!p.Alive || p.Ghost || p.StunTimer > 0 || p.InvincibleTimer > 0 || p.Tier != b.Tier) continue;
            if (Dist2(nx, nz, p.X, p.Z) < (PlayerRadius + BlastBomb.Radius) * (PlayerRadius + BlastBomb.Radius))
            {
                StunPlayer(p, 1.5f);
                b.State = BombState.Resting;
                b.VX = 0; b.VZ = 0;
                return;
            }
        }

        foreach (var other in _bombs)
        {
            if (other == b || other.State != BombState.Resting || other.Tier != b.Tier) continue;
            if (Dist2(nx, nz, other.X, other.Z) < (BlastBomb.Radius * 2 + 0.05f) * (BlastBomb.Radius * 2 + 0.05f))
            {
                // Billiard: the hit bomb slides in the same direction; this bomb stops.
                other.State = BombState.Rolling;
                other.VX = b.VX; other.VZ = b.VZ;
                b.State = BombState.Resting;
                b.VX = 0; b.VZ = 0;
                return;
            }
        }

        b.X = nx;
        b.Z = nz;
        b.CellX = cx;
        b.CellZ = cz;

        // Elimination: rolled off the stage.
        if (cell.IsElimination)
        {
            b.Exploded = true;
            DecrementOwnerBombs(b.OwnerId);
        }
    }

    private void TickFlyingBomb(ISessionContext context, BlastBomb b, float dt)
    {
        b.X += b.FlyVX * dt;
        b.Z += b.FlyVZ * dt;
        b.FlyVY -= BlastBomb.Gravity * dt;
        b.FlyY += b.FlyVY * dt;

        var landingY = _stage.At((int)b.X, (int)b.Z).Tier;

        // A head hit is a bomb coming down onto a player's head; on the way up it clears the thrower.
        foreach (var p in _players.Values)
        {
            if (!p.Alive || p.Ghost || p.InvincibleTimer > 0 || p.Tier != b.Tier) continue;
            if (b.FlyVY > 0 || b.FlyY > p.Tier + HeadHeight) continue;
            if (Dist2(b.X, b.Z, p.X, p.Z) < (PlayerRadius + BlastBomb.Radius) * (PlayerRadius + BlastBomb.Radius))
            {
                // Head hit: stun the player, bomb bounces (continues).
                StunPlayer(p, 2.0f);
                b.FlyVY = MathF.Abs(b.FlyVY) * 0.6f; // bounce upward
            }
        }

        // Landed.
        if (b.FlyY <= landingY)
        {
            b.FlyY = landingY;
            var cell = _stage.At((int)b.X, (int)b.Z);
            if (cell.IsElimination || b.X < 0 || b.X >= _stage.Width || b.Z < 0 || b.Z >= _stage.Height)
            {
                b.Exploded = true;
                DecrementOwnerBombs(b.OwnerId);
            }
            else
            {
                b.State = BombState.Resting;
                b.CellX = (int)b.X;
                b.CellZ = (int)b.Z;
                b.Tier = landingY; // update tier to where it landed
                b.FlyVX = 0; b.FlyVZ = 0; b.FlyVY = 0;
            }
        }
    }

    private void UpdateCarriedBombPosition(BlastBomb b)
    {
        // Whoever holds it, which need not be the player who placed it.
        var carrier = _players.Values.FirstOrDefault(p => p.CarryingBombId == b.Id);
        if (carrier is null) return;
        b.X = carrier.X + carrier.FacingX * (PlayerRadius + BlastBomb.Radius + 0.05f);
        b.Z = carrier.Z + carrier.FacingZ * (PlayerRadius + BlastBomb.Radius + 0.05f);
        b.Tier = carrier.Tier;
        b.CellX = (int)b.X;
        b.CellZ = (int)b.Z;
    }

    // ─── explosions ───────────────────────────────────────────────────────────

    private void ExplodeBomb(ISessionContext context, BlastBomb bomb)
    {
        if (bomb.Exploded) return;
        bomb.Exploded = true;
        DecrementOwnerBombs(bomb.OwnerId);

        var r = bomb.BlastRadius;

        // Create hazard sphere.
        _hazards.Add(new BlastHazard
        {
            Id = NextHazardId(),
            X = bomb.X, Z = bomb.Z, Tier = bomb.Tier,
            Radius = r, IsRed = bomb.IsRed,
        });

        // Destroy soft blocks and drop items.
        DestroyBlocksInBlast(context, bomb.X, bomb.Z, r, bomb.IsRed, bomb.Tier);

        // Hit players.
        foreach (var p in _players.Values)
        {
            if (p.InvincibleTimer > 0) continue;
            if (!InBlast(bomb.X, bomb.Z, bomb.Tier, r, p.X, p.Z, p.Tier, bomb.IsRed)) continue;
            HitPlayer(context, p);
        }

        // Chain reactions: nearby bombs.
        foreach (var other in _bombs)
        {
            if (other.Exploded || other.State == BombState.Carried) continue;
            if (InBlast(bomb.X, bomb.Z, bomb.Tier, r + 0.5f, other.X, other.Z, other.Tier, true))
                ExplodeBomb(context, other);
        }
    }

    private void DestroyBlocksInBlast(ISessionContext context, float bx, float bz, float r, bool red, int tier)
    {
        var ri = (int)Math.Ceiling(r);
        var cx0 = (int)(bx - ri); var cx1 = (int)(bx + ri) + 1;
        var cz0 = (int)(bz - ri); var cz1 = (int)(bz + ri) + 1;

        for (var x = cx0; x <= cx1; x++)
            for (var z = cz0; z <= cz1; z++)
            {
                if (!_softBlocks.IsAlive(x, z)) continue;
                var dx = x + 0.5f - bx;
                var dz = z + 0.5f - bz;
                if (dx * dx + dz * dz > r * r) continue;
                if (!red && BlocksLOS(bx, bz, x + 0.5f, z + 0.5f)) continue;

                _softBlocks.Set(x, z, false);
                // Item drop (roughly 30%).
                if (context.Random.NextDouble() < 0.3)
                    DropItem(context, x, z, tier);
            }
    }

    private bool InBlast(float bx, float bz, int bTier, float r, float px, float pz, int pTier, bool red)
    {
        // Treat each tier unit as 1 unit of Y distance.
        var dx = px - bx; var dz = pz - bz; var dy = (pTier - bTier);
        var dist2 = dx * dx + dz * dz + dy * dy;
        if (dist2 > r * r) return false;
        if (!red && BlocksLOS(bx, bz, px, pz)) return false;
        return true;
    }

    /// <summary>Returns true if a hard pillar or standing soft block sits between two points.</summary>
    private bool BlocksLOS(float ax, float az, float bx, float bz)
    {
        // Step at 0.25-cell intervals along the segment.
        var dx = bx - ax; var dz = bz - az;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist < 0.01f) return false;
        var steps = Math.Max(2, (int)(dist / 0.25f));
        for (var i = 1; i < steps; i++)
        {
            var t = (float)i / steps;
            var cx = (int)(ax + dx * t);
            var cz = (int)(az + dz * t);
            if (_stage.At(cx, cz).Kind == CellKind.Pillar) return true;
            if (_softBlocks.IsAlive(cx, cz)) return true;
        }
        return false;
    }

    // ─── hazards ──────────────────────────────────────────────────────────────

    private void TickHazards(float dt)
    {
        for (var i = _hazards.Count - 1; i >= 0; i--)
        {
            _hazards[i].LifeTimer -= dt;
            if (_hazards[i].LifeTimer <= 0)
                _hazards.RemoveAt(i);
        }
    }

    // ─── items ────────────────────────────────────────────────────────────────

    private static readonly string[] ItemTypes = ["fire", "bomb", "remote", "red", "heart"];

    private void DropItem(ISessionContext context, int cx, int cz, int tier)
    {
        var type = ItemTypes[context.Random.Next(ItemTypes.Length)];
        _items.Add(new BlastItem { Id = NextItemId(), CellX = cx, CellZ = cz, Tier = tier, Type = type });
    }

    private void TickItems(ISessionContext context)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            // Check if any player (or ghost) walks over it.
            foreach (var p in _players.Values)
            {
                if (!p.Alive || p.PendingRoundStart) continue;
                if (p.Ghost)
                {
                    // Ghost touching an item destroys it without effect.
                    if (p.Tier == item.Tier && Dist2(p.X, p.Z, item.X, item.Z) < PlayerRadius * PlayerRadius)
                    {
                        _items.RemoveAt(i);
                        goto nextItem;
                    }
                    continue;
                }
                if (!p.Alive || p.Tier != item.Tier) continue;
                if (Dist2(p.X, p.Z, item.X, item.Z) < (PlayerRadius + 0.4f) * (PlayerRadius + 0.4f))
                {
                    ApplyItem(p, item.Type);
                    _items.RemoveAt(i);
                    goto nextItem;
                }
            }
            nextItem:;
        }
    }

    private static void ApplyItem(BlastPlayer p, string type)
    {
        switch (type)
        {
            case "fire":  p.FireLevel = Math.Clamp(p.FireLevel + 1, 1, 3); break;
            case "bomb":  p.BombCapacity = Math.Min(p.BombCapacity + 1, 8); break;
            case "remote": p.HasRemote = true; break;
            case "red":   p.HasRed = true; break;
            case "heart": p.HasHeart = true; break;
        }
    }

    // ─── player hits ──────────────────────────────────────────────────────────

    private void HitPlayer(ISessionContext context, BlastPlayer p)
    {
        if (!p.Alive || p.Ghost || p.StunTimer > 0) return;

        if (p.HasHeart)
        {
            p.HasHeart = false;
            p.InvincibleTimer = 2.0f;
            return;
        }

        Eliminate(null, p);
    }

    private void Eliminate(string? causedBy, BlastPlayer p)
    {
        if (!p.Alive) return;
        p.Alive = false;

        // Drop one collected item power-up (designer choice).
        DropPowerUpOnDeath(p);

        // Detach any ghost latched to this player.
        foreach (var g in _players.Values)
            if (g.Ghost && g.LatchedToId == p.Id)
                DetachGhost(g);

        if (!_ghostsEnabled || p.Ghost || _phase == RoundPhase.Hurry)
            return; // sudden death never creates a new interactive ghost

        // Become a ghost.
        p.Ghost = true;
        p.Alive = true;
        p.StunTimer = 0;
        p.InvincibleTimer = 2.0f;
        // Lose carried power-ups as ghost.
        p.HasRed = false; p.HasRemote = false;
        p.BombCapacity = 1; p.FireLevel = 1;
    }

    private void DropPowerUpOnDeath(BlastPlayer p)
    {
        string? type = null;
        if (p.HasHeart) type = "heart";
        else if (p.HasRed) type = "red";
        else if (p.HasRemote) type = "remote";
        else if (p.FireLevel > 1) type = "fire";
        else if (p.BombCapacity > 3) type = "bomb";

        if (type is null) return;
        _items.Add(new BlastItem { Id = NextItemId(), CellX = (int)p.X, CellZ = (int)p.Z, Tier = p.Tier, Type = type });
    }

    private static void StunPlayer(BlastPlayer p, float duration)
    {
        if (p.InvincibleTimer > 0) return;
        p.StunTimer = duration;
        p.InvincibleTimer = duration + 1.5f;
    }

    // ─── ghost latch ─────────────────────────────────────────────────────────

    private void TryLatch(BlastPlayer ghost)
    {
        foreach (var p in _players.Values)
        {
            if (p.Ghost || !p.Alive || p.LatchedToId is not null) continue;
            if (p.Tier != ghost.Tier) continue;
            if (Dist2(ghost.X, ghost.Z, p.X, p.Z) > (PlayerRadius * 2) * (PlayerRadius * 2)) continue;

            // Already claimed by another ghost?
            if (_players.Values.Any(g => g.Ghost && g.LatchedToId == p.Id)) continue;

            ghost.LatchedToId = p.Id;
            ghost.LatchTimer = 4f;
            ghost.LatchReverseCount = 0;
            ghost.LatchPrevDX = p.FacingX;
            ghost.LatchPrevDZ = p.FacingZ;
            break;
        }
    }

    private void TrackLatch(BlastPlayer ghost, BlastPlayer victim, float dt)
    {
        // Ghost moves with the victim.
        ghost.X = victim.X;
        ghost.Z = victim.Z;
        ghost.Tier = victim.Tier;

        ghost.LatchTimer -= dt;

        // Track direction reversals for shake-off.
        ghost.LatchReverseCooldown -= dt;
        var vdx = victim.Input.X;
        var vdz = victim.Input.Z;
        if (vdx * vdx + vdz * vdz > 0.2f && ghost.LatchReverseCooldown <= 0)
        {
            if (vdx * ghost.LatchPrevDX + vdz * ghost.LatchPrevDZ < -0.5f)
            {
                ghost.LatchReverseCount++;
                ghost.LatchReverseCooldown = 0.15f;
                ghost.LatchPrevDX = vdx;
                ghost.LatchPrevDZ = vdz;
            }
        }

        // Shake-off: 6 reversals or 4 seconds elapsed.
        if (ghost.LatchReverseCount >= 6 || ghost.LatchTimer <= 0)
            DetachGhost(ghost);
    }

    private static void DetachGhost(BlastPlayer ghost)
    {
        ghost.LatchedToId = null;
        ghost.LatchTimer = 0;
        ghost.LatchReverseCount = 0;
        ghost.StunTimer = 0.5f; // give the victim time to move away before another latch
    }

    private static void DetachIfLatched(BlastPlayer p)
    {
        if (p.LatchedToId is not null) DetachGhost(p);
    }

    // ─── soft blocks ─────────────────────────────────────────────────────────

    private void PlaceSoftBlocks(Random rng)
    {
        _softBlocks = new SoftBlockGrid(_stage.Width, _stage.Height);
        var spawnCells = new HashSet<(int, int)>(_stage.Spawns.SelectMany(s =>
            new[] { (s.X, s.Z), (s.X + 1, s.Z), (s.X - 1, s.Z), (s.X, s.Z + 1), (s.X, s.Z - 1) }));

        for (var x = 0; x < _stage.Width; x++)
            for (var z = 0; z < _stage.Height; z++)
            {
                var cell = _stage.At(x, z);
                if (cell.Kind != CellKind.Floor) continue;
                if (spawnCells.Contains((x, z))) continue;
                if (rng.NextDouble() < 0.45)
                    _softBlocks.Set(x, z, true);
            }
    }

    // ─── hurry: column closure ────────────────────────────────────────────────

    private void KillInClosedColumns(ISessionContext context)
    {
        foreach (var p in _players.Values)
        {
            if (!p.Alive || p.Ghost) continue;
            if ((int)p.X < _closedLeft || (int)p.X >= _stage.Width - _closedRight)
                HitPlayer(context, p);
        }
        // Also explode resting bombs in closed columns.
        foreach (var b in _bombs.ToList())
        {
            if (b.State != BombState.Resting) continue;
            if (b.CellX < _closedLeft || b.CellX >= _stage.Width - _closedRight)
                ExplodeBomb(context, b);
        }
    }

    // ─── bot AI ──────────────────────────────────────────────────────────────

    private void BotThink(BlastPlayer bot, float dt)
    {
        bot.BotThinkTimer -= dt;
        if (bot.BotThinkTimer > 0)
        {
            // Apply last decided input.
            return;
        }
        bot.BotThinkTimer = 0.2f; // re-evaluate every 200 ms

        // Simple danger map: find the nearest safe cell.
        var (tx, tz) = FindBotTarget(bot);
        var dx = tx - bot.X;
        var dz = tz - bot.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.3f)
        {
            // Arrived at cell: try placing a bomb.
            if (!BotIsInDanger(bot) && bot.BotPlacingBomb)
            {
                bot.Input = new InputState(0, 0, A: true, B: false, R: false);
                bot.BotPlacingBomb = false;
                return;
            }
        }

        if (len > 0.01f) dx /= len; else dz = 1;
        bot.Input = new InputState(dx, dz, A: false, B: false, R: false);
    }

    private bool BotIsInDanger(BlastPlayer bot)
    {
        foreach (var b in _bombs)
        {
            if (b.State != BombState.Resting) continue;
            var dx = bot.X - b.X; var dz = bot.Z - b.Z;
            if (dx * dx + dz * dz < (b.BlastRadius + PlayerRadius) * (b.BlastRadius + PlayerRadius))
                return true;
        }
        return false;
    }

    private (float X, float Z) FindBotTarget(BlastPlayer bot)
    {
        if (BotIsInDanger(bot))
        {
            // Flee: find the nearest non-dangerous cell.
            var cx = (int)bot.X; var cz = (int)bot.Z;
            for (var r = 1; r <= 5; r++)
                for (var ox = -r; ox <= r; ox++)
                    for (var oz = -r; oz <= r; oz++)
                    {
                        if (Math.Abs(ox) != r && Math.Abs(oz) != r) continue;
                        var nx = cx + ox; var nz = cz + oz;
                        if (_stage.At(nx, nz).IsPassable && !_softBlocks.IsAlive(nx, nz))
                        {
                            var tx = nx + 0.5f; var tz = nz + 0.5f;
                            if (!BotIsTargetDangerous(tx, tz))
                                return (tx, tz);
                        }
                    }
        }

        // Look for a soft block adjacent to a player.
        foreach (var p in _players.Values)
        {
            if (p.Id == bot.Id || !p.Alive || p.Ghost) continue;
            foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var ax = (int)p.X + ox; var az = (int)p.Z + oz;
                if (_softBlocks.IsAlive(ax, az))
                {
                    bot.BotPlacingBomb = true;
                    return (ax + 0.5f, az + 0.5f);
                }
            }
        }

        // Wander toward a random player.
        var target = _players.Values.FirstOrDefault(p => p.Id != bot.Id && p.Alive && !p.Ghost);
        if (target is not null) return (target.X, target.Z);
        return (bot.X, bot.Z);
    }

    private bool BotIsTargetDangerous(float tx, float tz)
    {
        foreach (var b in _bombs)
        {
            if (b.State != BombState.Resting) continue;
            var dx = tx - b.X; var dz = tz - b.Z;
            if (dx * dx + dz * dz < (b.BlastRadius + PlayerRadius) * (b.BlastRadius + PlayerRadius))
                return true;
        }
        return false;
    }

    // ─── stage message ────────────────────────────────────────────────────────

    private void SendStage(ISessionContext context, string playerId)
    {
        var w = _stage.Width;
        var h = _stage.Height;
        var flat = new object[w * h];
        for (var z = 0; z < h; z++)
            for (var x = 0; x < w; x++)
            {
                var c = _stage.At(x, z);
                flat[z * w + x] = new
                {
                    k = (int)c.Kind,
                    t = c.Tier,
                    rx = c.RampDX,
                    rz = c.RampDZ,
                    rf = c.RampFromTier,
                    rt = c.RampToTier,
                };
            }

        context.Send(playerId, "stage", new
        {
            id = _stage.Id,
            name = _stage.Name,
            width = w,
            height = h,
            cells = flat,
            spawns = _stage.Spawns.Select(s => new[] { s.X, s.Z }),
            round = _roundNum,
            winsTarget = _winsTarget,
            roundSeconds = _roundSeconds,
        });
    }

    // ─── state broadcast ──────────────────────────────────────────────────────

    private void BroadcastState(ISessionContext context)
    {
        var playerList = _players.Values.Select(p => new
        {
            slot = p.Slot,
            id = p.Id,
            name = p.DisplayName,
            bot = p.IsBot,
            x = MathF.Round(p.X, 2),
            y = (float)p.Tier,
            z = MathF.Round(p.Z, 2),
            fx = MathF.Round(p.FacingX, 2),
            fz = MathF.Round(p.FacingZ, 2),
            alive = p.Alive,
            ghost = p.Ghost,
            stunned = p.StunTimer > 0,
            carrying = p.CarryingBombId is not null ? "bomb" : p.CarryingPlayerId is not null ? "player" : null,
            heart = p.HasHeart,
            fire = p.FireLevel,
            bombs = p.BombCapacity,
            red = p.HasRed,
            remote = p.HasRemote,
            wins = p.Wins,
            ack = context.LastSequence(p.Id),
        });

        var bombList = _bombs.Where(b => !b.Exploded).Select(b => new
        {
            id = b.Id,
            x = MathF.Round(b.X, 2),
            y = b.FlyY > 0 ? MathF.Round(b.FlyY, 2) : (float)b.Tier,
            z = MathF.Round(b.Z, 2),
            level = b.FireLevel,
            pumps = b.Pumps,
            red = b.IsRed,
            remote = b.IsRemote,
            fuse = MathF.Round(b.FuseTimer, 2),
            state = (int)b.State,
        });

        var hazardList = _hazards.Select(h => new
        {
            id = h.Id,
            x = MathF.Round(h.X, 2),
            z = MathF.Round(h.Z, 2),
            r = MathF.Round(h.Radius, 2),
        });

        var itemList = _items.Select(it => new
        {
            id = it.Id,
            cx = it.CellX,
            cz = it.CellZ,
            type = it.Type,
        });

        var timeLeft = Math.Max(0, _roundSeconds - _roundTimer);

        context.Broadcast("state", new
        {
            round = _roundNum,
            phase = (int)_phase,
            timeLeft = MathF.Round(timeLeft, 1),
            closedLeft = _closedLeft,
            closedRight = _closedRight,
            players = playerList,
            bombs = bombList,
            hazards = hazardList,
            items = itemList,
            softBlocks = _softBlocks.ToBase64(),
        });
    }

    // ─── scoreboard ───────────────────────────────────────────────────────────

    private JsonObject BuildScoreboard(string reason)
    {
        var rows = new JsonArray(_players.Values.OrderByDescending(p => p.Wins)
            .Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.DisplayName,
                ["wins"] = p.Wins,
                ["bot"] = p.IsBot,
            }).ToArray());
        return new JsonObject { ["reason"] = reason, ["scoreboard"] = rows };
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static float Dist2(float ax, float az, float bx, float bz) =>
        (ax - bx) * (ax - bx) + (az - bz) * (az - bz);

    private static float Finite(float v) => float.IsFinite(v) ? Math.Clamp(v, -2f, 2f) : 0f;

    private void DecrementOwnerBombs(string? ownerId)
    {
        if (ownerId is not null && _players.TryGetValue(ownerId, out var owner))
            owner.ActiveBombs = Math.Max(0, owner.ActiveBombs - 1);
    }

    private void DropCarriedBombs(BlastPlayer p)
    {
        if (p.CarryingBombId is not null)
        {
            var b = _bombs.FirstOrDefault(b => b.Id == p.CarryingBombId);
            if (b is not null) { b.State = BombState.Resting; b.CellX = (int)b.X; b.CellZ = (int)b.Z; }
            p.CarryingBombId = null;
        }
        if (p.CarryingPlayerId is not null)
        {
            if (_players.TryGetValue(p.CarryingPlayerId, out var held))
                held.StunTimer = 0;
            p.CarryingPlayerId = null;
        }
    }

    private int FindFreeSlot()
    {
        var used = _players.Values.Select(p => p.Slot).ToHashSet();
        for (var i = 0; i < 256; i++)
            if (!used.Contains(i)) return i;
        return 0;
    }

    private static int TryInt(GameSessionOptions opts, string key, int def, int min, int max) =>
        opts.Settings.TryGetValue(key, out var s) && int.TryParse(s, out var v)
            ? Math.Clamp(v, min, max) : def;

    private static bool TryBool(GameSessionOptions opts, string key, bool def) =>
        opts.Settings.TryGetValue(key, out var s) ? s is "on" or "true" or "1" : def;

    /// <summary>JSON shape of the client's input message.</summary>
    private sealed class InputMsg
    {
        public float X { get; set; }
        public float Z { get; set; }
        public bool A { get; set; }
        public bool B { get; set; }
        public bool R { get; set; }
    }

    // ─── internal test seams (InternalsVisibleTo Burbuja.Realtime.Tests) ────────
    // These methods expose mutable simulation state for deterministic unit tests.
    // They MUST NOT be called from production code paths.

    /// <summary>Direct read of a player entity by id. Returns null if not found.</summary>
    internal BlastPlayer? TestPlayer(string id) =>
        _players.TryGetValue(id, out var p) ? p : null;

    /// <summary>Read-only view of the live bomb list.</summary>
    internal IReadOnlyList<BlastBomb> TestBombs => _bombs;

    /// <summary>Read-only view of the live hazard list.</summary>
    internal IReadOnlyList<BlastHazard> TestHazards => _hazards;

    /// <summary>Teleport a player to (x, z, tier) without collision checks. For test setup only.</summary>
    internal void TestPlacePlayer(string id, float x, float z, int tier = 0)
    {
        if (_players.TryGetValue(id, out var p))
        { p.X = x; p.Z = z; p.Tier = tier; }
    }

    /// <summary>
    /// Insert a bomb directly into the live bomb list, bypassing placement rules.
    /// <paramref name="ownerId"/> may be null (ownerless bomb).
    /// </summary>
    internal BlastBomb TestPlaceBomb(string? ownerId, int cellX, int cellZ,
        int tier = 0, int fireLevel = 1, float fuse = BlastBomb.FuseSeconds)
    {
        var bomb = new BlastBomb
        {
            Id = NextBombId(),
            OwnerId = ownerId,
            X = cellX + 0.5f,
            Z = cellZ + 0.5f,
            CellX = cellX,
            CellZ = cellZ,
            Tier = tier,
            FireLevel = fireLevel,
            FuseTimer = fuse,
        };
        _bombs.Add(bomb);
        if (ownerId is not null && _players.TryGetValue(ownerId, out var owner))
            owner.ActiveBombs++;
        return bomb;
    }

    /// <summary>Remove all soft blocks from the grid. For test setup only.</summary>
    internal void TestClearSoftBlocks()
    {
        for (var x = 0; x < _stage.Width; x++)
            for (var z = 0; z < _stage.Height; z++)
                _softBlocks.Set(x, z, false);
    }
}
