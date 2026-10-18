using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Realtime.Abstractions;
using Example.Blast.Plugin;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// Deterministic simulation tests for the Blast game. All tests drive the session directly
/// via its internal test seams. No real time, network or randomness beyond the fixed seed.
/// </summary>
public sealed class BlastSimTests
{
    [Fact]
    public void A_human_joining_during_ready_waits_for_the_next_round()
    {
        var ctx = new FakeBlastContext(1);
        var session = new BlastSession(MakeOptions(new() { ["bots"] = "7" }));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("a", 0));
        session.OnMatchStarted(ctx);
        session.OnPlayerJoined(ctx, ctx.AddPlayer("late", 1));
        var late = session.TestPlayer("late")!;
        Assert.True(late.PendingRoundStart);
        Assert.False(late.Alive);
        TickFor(session, ctx, 2.1f);
        Assert.True(late.PendingRoundStart);
        Assert.False(late.Alive);
    }

    [Fact]
    public void A_ghost_overrides_movement_but_cannot_press_the_victims_action_buttons()
    {
        var ctx = new FakeBlastContext(1);
        var session = CreateSession(ctx);
        var victim = ctx.AddPlayer("victim", 0);
        session.OnPlayerJoined(ctx, victim);
        session.OnPlayerJoined(ctx, ctx.AddPlayer("other", 1));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("ghost", 2));
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();
        TickFor(session, ctx, 2.1f);
        session.TestPlacePlayer("victim", 5.5f, 5.5f);
        var ghost = session.TestPlayer("ghost")!;
        ghost.Ghost = true;
        ghost.LatchedToId = "victim";
        ghost.LatchTimer = 4;
        ghost.Input = new InputState(0, 0, true, true, true);
        TickFor(session, ctx, 0.04f);
        Assert.DoesNotContain(session.TestBombs, b => b.OwnerId == "victim");
        session.OnMessage(ctx, victim, TextMsg("input", new { x = 0, z = 0, a = true }, 1));
        TickFor(session, ctx, 0.04f);
        Assert.Contains(session.TestBombs, b => b.OwnerId == "victim");
    }

    [Fact]
    public void A_late_human_replaces_the_bot_in_their_slot_and_waits_without_interacting()
    {
        var ctx = new FakeBlastContext(1);
        var session = new BlastSession(MakeOptions(new() { ["bots"] = "7" }));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("a", 0));
        session.OnMatchStarted(ctx);
        TickFor(session, ctx, 2.1f);
        var late = ctx.AddPlayer("late", 1);
        session.OnPlayerJoined(ctx, late);
        Assert.Null(session.TestPlayer("bot-0"));
        var spectator = session.TestPlayer("late")!;
        Assert.True(spectator.PendingRoundStart);
        var x = spectator.X;
        session.OnMessage(ctx, late, TextMsg("input", new { x = 1, z = 0, a = true, b = true }, 1));
        TickFor(session, ctx, 0.1f);
        Assert.Equal(x, spectator.X);
        Assert.DoesNotContain(session.TestBombs, b => b.OwnerId == "late");
    }

    [Fact]
    public void The_victims_six_reversals_shake_off_a_ghost_without_overwriting_human_input()
    {
        var ctx = new FakeBlastContext(1);
        var session = CreateSession(ctx);
        var victim = ctx.AddPlayer("victim", 0);
        session.OnPlayerJoined(ctx, victim);
        session.OnPlayerJoined(ctx, ctx.AddPlayer("other", 1));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("ghost", 2));
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();
        TickFor(session, ctx, 2.1f);
        var ghost = session.TestPlayer("ghost")!;
        ghost.Ghost = true;
        ghost.LatchedToId = "victim";
        ghost.LatchTimer = 4;
        ghost.LatchPrevDX = 1;
        for (var i = 0; i < 6; i++)
        {
            var direction = i % 2 == 0 ? -1 : 1;
            session.OnMessage(ctx, victim, TextMsg("input", new { x = direction, z = 0 }, i));
            TickFor(session, ctx, 0.2f);
            Assert.Equal(direction, session.TestPlayer("victim")!.Input.X);
            if (i < 5) Assert.Equal(i + 1, ghost.LatchReverseCount);
        }
        Assert.Null(ghost.LatchedToId);
    }

    [Fact]
    public void Hurry_removes_ghosts_and_their_latches_even_when_input_is_held()
    {
        var ctx = new FakeBlastContext(1);
        var session = new BlastSession(MakeOptions(new() { ["round-seconds"] = "60" }));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("victim", 0));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("other", 1));
        session.OnPlayerJoined(ctx, ctx.AddPlayer("ghost", 2));
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();
        var ghost = session.TestPlayer("ghost")!;
        ghost.Ghost = true;
        ghost.LatchedToId = "victim";
        ghost.LatchTimer = 4;
        ghost.Input = new InputState(1, 0, true, true, false);
        // A 60-second round enters Hurry on its first Fight tick.
        TickFor(session, ctx, 2.1f);
        var x = ghost.X;
        TickFor(session, ctx, 0.1f);
        Assert.False(ghost.Alive);
        Assert.False(ghost.Ghost);
        Assert.Null(ghost.LatchedToId);
        Assert.Equal(x, ghost.X);
    }

    // ─── 1. Bomb explodes after fuse, eliminates player in range ─────────────

    [Fact]
    public void Bomb_explodes_after_fuse_and_eliminates_nearby_player()
    {
        var ctx = new FakeBlastContext(seed: 1);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        // p1 far away; bomb placed directly at cell (5,5); p2 within blast radius (0.5 cells).
        session.TestPlacePlayer("a", 2.5f, 2.5f);
        session.TestPlacePlayer("b", 6.0f, 5.5f);

        TickFor(session, ctx, 2.1f); // past Ready, enter Fight

        // Place bomb at cell (5,5), center (5.5, 5.5). p2 is 0.5 cells away (< radius 1.3).
        session.TestPlaceBomb(null, 5, 5);

        TickFor(session, ctx, 2.6f); // past 2.5 s fuse

        // No pillar between (5.5,5.5) and (6.0,5.5); p2 must become a ghost.
        Assert.True(session.TestPlayer("b")!.Ghost, "p2 must become a ghost after bomb blast");
        // p1 is far enough to survive.
        Assert.False(session.TestPlayer("a")!.Ghost, "p1 must survive (too far from blast)");
    }

    // ─── 2. Pillar on line-of-sight shields player from blast ─────────────────

    [Fact]
    public void Bomb_does_not_eliminate_player_shielded_by_a_pillar()
    {
        // Pillar at (7,5) in RockGarden sits between bomb at (5,5) and p2 at (8,5).
        // A fireLevel-3 bomb (radius 3.1) reaches p2 by distance but BlocksLOS must block it.
        var ctx = new FakeBlastContext(seed: 42);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1); var p3 = ctx.AddPlayer("c", 2);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2); session.OnPlayerJoined(ctx, p3);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        session.TestPlacePlayer("a", 2.5f, 2.5f);
        session.TestPlacePlayer("b", 8.5f, 5.5f); // right of pillar at (7,5)
        session.TestPlacePlayer("c", 5.5f, 4.5f); // the control: in range with nothing in between

        TickFor(session, ctx, 2.1f);

        // fireLevel=3 → radius = 3*0.9 + 0.4 = 3.1 cells; p2 is at dist 3.0 < 3.1 (in range).
        session.TestPlaceBomb(null, 5, 5, fireLevel: 3);

        TickFor(session, ctx, 2.6f);

        // Pillar at cell (7,5) must block the blast; p2 must be alive, not a ghost.
        var p2state = session.TestPlayer("b")!;
        Assert.True(p2state.Alive, "p2 must still be alive behind the pillar");
        Assert.False(p2state.Ghost, "p2 must not be a ghost behind the pillar");
        var p3state = session.TestPlayer("c")!;
        Assert.True(!p3state.Alive || p3state.Ghost, "the unshielded player in range must be eliminated");
    }

    // ─── 3. Chain reaction: adjacent bomb detonates in the same tick ─────────

    [Fact]
    public void Chain_reaction_detonates_adjacent_bomb_immediately()
    {
        var ctx = new FakeBlastContext(seed: 1);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        // Both players safe, far from the bomb pair.
        session.TestPlacePlayer("a", 2.5f, 2.5f);
        session.TestPlacePlayer("b", 2.5f, 3.5f);

        TickFor(session, ctx, 2.1f);

        // Two adjacent bombs at (5,5) and (6,5), 1 cell apart. The second would not go off for 100 s on its own.
        session.TestPlaceBomb(null, 5, 5);
        var second = session.TestPlaceBomb(null, 6, 5, fuse: 100f);

        TickFor(session, ctx, 2.55f);

        // The first blast set off the second long before its fuse.
        Assert.True(second.Exploded || !session.TestBombs.Contains(second), "the second bomb must have chained");
        Assert.Equal(2, session.TestHazards.Count);
    }

    // ─── 4. Kick rolls a bomb; rolling bomb stuns the player it reaches ───────

    [Fact]
    public void Kick_starts_rolling_and_rolling_bomb_stuns_target()
    {
        var ctx = new FakeBlastContext(seed: 5);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        // z=4 row has no pillars in RockGarden.
        // p1 is slightly left of the bomb; p2 is to the right of the bomb.
        // p1 at exactly 0.75 cells from bomb center (kick threshold is ≤ 0.80).
        session.TestPlacePlayer("a", 4.75f, 4.5f);
        session.TestPlacePlayer("b", 8.5f,  4.5f);
        // Bomb owned by p2 so p1 has no owner pass-through; long fuse prevents explosion.
        session.TestPlaceBomb("b", 5, 4, fuse: 100f);

        // Give p1 a rightward input.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 1, z = 0, a = false, b = false, r = false }, 1));

        TickFor(session, ctx, 2.1f); // past Ready; first Fight tick kicks the bomb

        // Bomb must have started rolling in the first Fight tick.
        var bomb = session.TestBombs[0];
        Assert.Equal(BombState.Rolling, bomb.State);
        Assert.True(bomb.VX > 0, "bomb must roll in +x direction");

        // The kicker stops; the bomb rolls on alone.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = false, b = false, r = false }, 2));

        TickFor(session, ctx, 0.7f); // ~21 ticks; bomb travels to p2 and stuns

        Assert.True(session.TestPlayer("b")!.StunTimer > 0, "p2 must be stunned by the rolling bomb");
    }

    // ─── 5. Pick up, pump ×2, throw; bomb stuns player it hits mid-flight ─────

    [Fact]
    public void Pick_up_pump_and_throw_stuns_player_on_head_hit()
    {
        var ctx = new FakeBlastContext(seed: 7);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        // p1 at (4.5, 4.5); p2 at throw-arc landing zone (6 cells forward at speed 6 ≈ tick 27 hit).
        session.TestPlacePlayer("a", 4.5f, 4.5f);
        session.TestPlacePlayer("b", 10.5f, 4.5f);
        var bomb = session.TestPlaceBomb(null, 4, 4, fuse: 100f);

        TickFor(session, ctx, 2.1f); // past Ready

        // B press → pick up.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = false, b = true, r = false }, 1));
        ctx.AdvanceTick(1); session.OnTick(ctx);
        Assert.NotNull(session.TestPlayer("a")!.CarryingBombId);

        // A-down, A-up, A-down → two pumps.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = true,  b = true, r = false }, 2));
        ctx.AdvanceTick(2); session.OnTick(ctx); // pump 1
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = false, b = true, r = false }, 3));
        ctx.AdvanceTick(3); session.OnTick(ctx);
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = true,  b = true, r = false }, 4));
        ctx.AdvanceTick(4); session.OnTick(ctx); // pump 2

        Assert.Equal(2, bomb.Pumps);

        // B release with rightward stick → throw. Facing updates to (+1,0) in same tick.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 1, z = 0, a = false, b = false, r = false }, 5));
        ctx.AdvanceTick(5); session.OnTick(ctx); // throw

        Assert.Equal(BombState.Flying, bomb.State);

        // Tick 35 frames. Bomb hits p2 at ~tick 27 (horizontal dist closes within 0.75).
        TickFor(session, ctx, 35f / 30f);

        Assert.True(session.TestPlayer("b")!.StunTimer > 0,
            "p2 must be stunned by a head hit during bomb flight");
        Assert.Equal(2, bomb.Pumps); // pumps preserved after throw
    }

    // ─── 6. Each pump grows the blast radius ─────────────────────────────────

    [Fact]
    public void Each_pump_increases_blast_radius()
    {
        var ctx = new FakeBlastContext(seed: 6);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);

        // p1 spawns at (1.5, 1.5). Place bomb at cell (1,1) for easy pick-up.
        var bomb = session.TestPlaceBomb(null, 1, 1, fuse: 100f);

        TickFor(session, ctx, 2.1f); // past Ready

        // B press → pick up.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = false, b = true, r = false }, 1));
        ctx.AdvanceTick(1); session.OnTick(ctx);

        var radiusBefore = bomb.BlastRadius;

        // Three A presses (with releases between) pump the bomb three times.
        for (var seq = 2; seq <= 7; seq++)
        {
            var aDown = seq % 2 == 0; // down on even ticks 2,4,6; up on 3,5,7
            session.OnMessage(ctx, p1,
                TextMsg("input", new { x = 0, z = 0, a = aDown, b = true, r = false }, seq));
            ctx.AdvanceTick(seq); session.OnTick(ctx);
        }

        // BlastRadius = fireLevel*0.9 + pumps*0.75 + 0.4. At fireLevel=1, pumps=3: 3.55.
        Assert.Equal(3, bomb.Pumps);
        Assert.True(bomb.BlastRadius > radiusBefore,
            "blast radius must increase after pumping");
        Assert.Equal(1 * 0.9f + 3 * 0.75f + 0.4f, bomb.BlastRadius, precision: 3);

        // State broadcast must reflect the pumps count.
        ctx.AdvanceTick(8); session.OnTick(ctx);
        ctx.AdvanceTick(9); session.OnTick(ctx); // ensure even tick for broadcast
        var stateNode = ctx.LastBroadcast("state")!["bombs"]!.AsArray();
        Assert.Equal(3, (int)stateNode[0]!["pumps"]!);
    }

    // ─── 7. Heart absorbs exactly one hit; second hit eliminates ─────────────

    [Fact]
    public void Heart_absorbs_one_hit_and_second_hit_eliminates()
    {
        var ctx = new FakeBlastContext(seed: 7);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        session.TestPlacePlayer("a", 2.5f, 2.5f);
        session.TestPlacePlayer("b", 6.0f, 5.5f);
        session.TestPlayer("b")!.HasHeart = true; // give p2 a heart

        TickFor(session, ctx, 2.1f); // past Ready

        // First bomb: heart absorbs the hit.
        session.TestPlaceBomb(null, 5, 5);
        TickFor(session, ctx, 2.6f); // fuse + explosion

        var p2state = session.TestPlayer("b")!;
        Assert.False(p2state.Ghost,  "heart must absorb the first hit; p2 must not become a ghost");
        Assert.True(p2state.Alive,   "p2 must remain alive after heart absorbs hit");
        Assert.False(p2state.HasHeart, "heart must be consumed");

        // Wait for invincibility to expire (2 s) then place second bomb.
        TickFor(session, ctx, 2.5f);

        session.TestPlaceBomb(null, 5, 5);
        TickFor(session, ctx, 2.6f); // second explosion

        // Without a heart, second hit eliminates p2.
        Assert.True(session.TestPlayer("b")!.Ghost, "second hit without heart must make p2 a ghost");
    }

    // ─── 8. Walking off the edge eliminates a player ─────────────────────────

    [Fact]
    public void Walking_off_grid_edge_eliminates_player()
    {
        var ctx = new FakeBlastContext(seed: 2);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        // p1 safe; p2 close to left edge (x=0).
        session.TestPlacePlayer("a", 8.5f, 7.5f);
        session.TestPlacePlayer("b", 0.2f, 7.5f);

        // Drive p2 left; two Fight ticks cross x=0 → elimination.
        session.OnMessage(ctx, p2, TextMsg("input", new { x = -1, z = 0, a = false, b = false, r = false }, 1));
        TickFor(session, ctx, 2.1f + 0.1f); // past Ready + 3 Fight ticks

        Assert.True(session.TestPlayer("b")!.Ghost, "p2 must become a ghost after walking off the edge");
        Assert.False(session.TestPlayer("a")!.Ghost, "p1 must not be affected");
    }

    // ─── 9. Last standing wins the round; wins=1 ends the match ──────────────

    [Fact]
    public void Last_standing_wins_round_and_match_ends_when_wins_target_reached()
    {
        var opts = MakeOptions(new Dictionary<string, string> { ["wins"] = "1" });
        var ctx = new FakeBlastContext(seed: 9);
        var session = new BlastSession(opts);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        session.TestPlacePlayer("a", 8.5f, 7.5f);
        session.TestPlacePlayer("b", 0.2f, 7.5f);

        session.OnMessage(ctx, p2, TextMsg("input", new { x = -1, z = 0, a = false, b = false, r = false }, 1));
        TickFor(session, ctx, 2.1f + 0.1f); // p2 falls off

        // With wins=1 and p1 as last standing, EndMatch must be called with p1 as winner.
        Assert.True(ctx.EndMatchCalled, "EndMatch must be called when wins target is reached");
        Assert.Equal("won", ctx.LastMatchResult!.Reason);
        Assert.Contains("a", ctx.LastMatchResult.Winners);
    }

    // ─── 10. Sudden death closes columns and kills players inside them ────────

    [Fact]
    public void Sudden_death_closes_columns_and_kills_player_standing_in_closed_column()
    {
        // round-seconds=70 → hurry triggers when roundTimer >= 10 (timeLeft <= 60).
        // After 1 s of hurry, closedLeft=1; player in column 0 (int(x) < 1) is killed.
        var opts = MakeOptions(new Dictionary<string, string>
        {
            ["wins"] = "3", ["round-seconds"] = "70", ["sudden-death"] = "on",
        });
        var ctx = new FakeBlastContext(seed: 9);
        var session = new BlastSession(opts);
        var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
        session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        session.TestPlacePlayer("a", 8.5f, 7.5f); // safe
        session.TestPlacePlayer("b", 0.5f, 7.5f); // in column 0

        // Skip Ready (2 s), 10 s of Fight until hurry, then 1.1 s of hurry → column closes.
        TickFor(session, ctx, 2.1f);   // past Ready
        TickFor(session, ctx, 10.1f);  // hurry triggered (timeLeft <= 60)
        TickFor(session, ctx, 1.1f);   // first column closes

        var state = ctx.LastBroadcast("state")!;
        Assert.True((int)state["closedLeft"]! >= 1, "at least one column must be closed");
        Assert.True(session.TestPlayer("b")!.Ghost,
            "p2 standing in closed column must be eliminated");
    }

    // ─── 11. Ghost cannot place new bombs ────────────────────────────────────

    [Fact]
    public void Ghost_cannot_place_bombs()
    {
        // Need 3 players so the round does not end when p1 becomes a ghost.
        var ctx = new FakeBlastContext(seed: 11);
        var session = CreateSession(ctx);
        var p1 = ctx.AddPlayer("a", 0);
        var p2 = ctx.AddPlayer("b", 1);
        var p3 = ctx.AddPlayer("c", 2);
        session.OnPlayerJoined(ctx, p1);
        session.OnPlayerJoined(ctx, p2);
        session.OnPlayerJoined(ctx, p3);
        session.OnMatchStarted(ctx);
        session.TestClearSoftBlocks();

        session.TestPlacePlayer("a", 0.2f, 7.5f); // near edge
        session.TestPlacePlayer("b", 8.5f, 7.5f);
        session.TestPlacePlayer("c", 8.5f, 4.5f);

        // Drive p1 off the left edge → p1 becomes a ghost; living = 2 (b,c) → round continues.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = -1, z = 0, a = false, b = false, r = false }, 1));
        TickFor(session, ctx, 2.1f + 0.1f);

        Assert.True(session.TestPlayer("a")!.Ghost, "p1 must be a ghost (test precondition)");

        var bombsBefore = session.TestBombs.Count;

        // Ghost presses A — must not place a new bomb.
        session.OnMessage(ctx, p1, TextMsg("input", new { x = 0, z = 0, a = true, b = false, r = false }, 2));
        ctx.AdvanceTick(0); session.OnTick(ctx);

        Assert.Equal(bombsBefore, session.TestBombs.Count); // ghost pressing A must not add new bombs
    }

    // ─── 12. bots=7 + 1 human yields 8 players; MinPlayersToStart ────────────

    [Fact]
    public void Bots_fill_arena_to_eight_players_and_MinPlayersToStart_is_one()
    {
        var optsWithBots = MakeOptions(new Dictionary<string, string> { ["bots"] = "7" });
        Assert.Equal(1, new BlastSession(optsWithBots).MinPlayersToStart);

        var optsNoBots = MakeOptions(new Dictionary<string, string>());
        Assert.Null(new BlastSession(optsNoBots).MinPlayersToStart);

        // One human + 7 CPU bots must yield exactly 8 players in the state.
        var ctx = new FakeBlastContext(seed: 42);
        var session = new BlastSession(optsWithBots);
        var human = ctx.AddPlayer("h", 0);
        session.OnPlayerJoined(ctx, human);
        session.OnMatchStarted(ctx);

        ctx.AdvanceTick(0); session.OnTick(ctx);
        ctx.AdvanceTick(1); session.OnTick(ctx); // even tick → broadcast

        var players = ctx.LastBroadcast("state")!["players"]!.AsArray();
        Assert.Equal(8, players.Count);
    }

    // ─── 13. Determinism: same seed + same inputs → identical state hash ──────

    [Fact]
    public void Same_seed_and_same_inputs_produce_identical_state_hash()
    {
        static string RunAndHash(int seed)
        {
            var opts = new GameSessionOptions("lobby-test", "blast",
                new Dictionary<string, string> { ["wins"] = "3", ["bots"] = "2" }, seed);
            var ctx = new FakeBlastContext(seed);
            var session = new BlastSession(opts);
            var p1 = ctx.AddPlayer("a", 0); var p2 = ctx.AddPlayer("b", 1);
            session.OnPlayerJoined(ctx, p1); session.OnPlayerJoined(ctx, p2);
            session.OnMatchStarted(ctx);

            for (var i = 0; i < 90; i++)
            {
                if (i % 10 == 5)
                    session.OnMessage(ctx, p1,
                        TextMsg("input", new { x = 1, z = 0, a = i % 20 == 5, b = false, r = false }, i));
                ctx.AdvanceTick(i);
                session.OnTick(ctx);
            }
            var last = ctx.LastBroadcast("state");
            return HashOf(last?.ToJsonString() ?? "");
        }

        var h1 = RunAndHash(777);
        var h2 = RunAndHash(777);
        Assert.Equal(h1, h2);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static BlastSession CreateSession(FakeBlastContext ctx) =>
        new(MakeOptions(new Dictionary<string, string>()));

    private static GameSessionOptions MakeOptions(Dictionary<string, string> settings) =>
        new("lobby-test", "blast", settings, 42);

    private static void TickFor(BlastSession session, FakeBlastContext ctx, float seconds)
    {
        var ticks = (int)(seconds * 30);
        for (var i = 0; i < ticks; i++)
        {
            ctx.AdvanceTick(i);
            session.OnTick(ctx);
        }
    }

    private static InboundMessage TextMsg(string type, object payload, int seq)
    {
        var json = JsonSerializer.Serialize(payload);
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return new InboundMessage(type, element, seq, DateTimeOffset.UtcNow)
        {
            Format = FrameFormat.Text,
            SerializerOptions = RealtimeJson.Default,
        };
    }

    private static string HashOf(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}

// ─── Fake ISessionContext ──────────────────────────────────────────────────────

/// <summary>Minimal deterministic session context for unit tests.</summary>
internal sealed class FakeBlastContext : ISessionContext
{
    private readonly List<RealtimePlayer> _players = [];
    private readonly Dictionary<string, List<JsonObject>> _sent = new(StringComparer.Ordinal);
    private readonly List<(string Type, JsonObject Payload)> _broadcasts = [];
    private readonly Dictionary<string, long?> _seqs = new(StringComparer.Ordinal);
    private long _tick;

    public FakeBlastContext(int seed) { Random = new Random(seed); }

    public string LobbyId => "lobby-test";
    public string GameId => "blast";
    public LobbyState State { get; set; } = LobbyState.Running;
    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>();
    public IReadOnlyList<RealtimePlayer> Players => _players;
    public long Tick => _tick;
    public TimeSpan TickDelta => TimeSpan.FromSeconds(1.0 / 30);
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_tick / 30.0);
    public Random Random { get; }
    public bool EndMatchCalled { get; private set; }
    public MatchResult? LastMatchResult { get; private set; }

    public RealtimePlayer AddPlayer(string id, int slot)
    {
        var p = new RealtimePlayer(id, $"Player{slot}", slot, new Dictionary<string, string>());
        _players.Add(p);
        return p;
    }

    public void AdvanceTick(long n) => _tick = n;

    public bool IsConnected(string playerId) => true;

    public long? LastSequence(string playerId) =>
        _seqs.TryGetValue(playerId, out var s) ? s : null;

    public void Send<T>(string playerId, string type, T payload)
    {
        var json = JsonSerializer.Serialize(payload, RealtimeJson.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        if (!_sent.TryGetValue(type, out var list))
            _sent[type] = list = [];
        list.Add(node);
    }

    public void Broadcast<T>(string type, T payload, string? exceptPlayerId = null)
    {
        var json = JsonSerializer.Serialize(payload, RealtimeJson.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        _broadcasts.Add((type, node));
    }

    public void SendBinary(string playerId, string type, ReadOnlySpan<byte> payload) { }

    public void BroadcastBinary(string type, ReadOnlySpan<byte> payload, string? exceptPlayerId = null) { }

    public void EndMatch(MatchResult result)
    {
        EndMatchCalled = true;
        LastMatchResult = result;
    }

    public void Kick(string playerId, string reason) { }

    public JsonObject? LastBroadcast(string type) =>
        _broadcasts.LastOrDefault(b => b.Type == type).Payload;

    public void UpdateSeq(string playerId, long? seq)
    {
        if (seq.HasValue) _seqs[playerId] = seq;
    }
}
