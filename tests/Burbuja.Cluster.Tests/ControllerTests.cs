using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Controller;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-controller-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static WorkloadSpec Trainer(string name = "trainer") => Lab.Program(name, "trainer", WorkloadClass.Batch) with
    {
        Requests = new Dictionary<string, decimal> { ["gpu"] = 1, ["cpu"] = 8 },
        Placement = new PlacementSpec { RequiredCapabilities = ["cuda"] },
        Execution = new ExecutionProfile { Priority = ExecutionPriority.Background, YieldToInteractive = true },
    };

    private static WorkloadSpec Player(string name) => Lab.Program(name, "player", WorkloadClass.Interactive) with
    {
        Requests = new Dictionary<string, decimal> { ["player-slot"] = 1, ["cpu"] = 2 },
        Placement = new PlacementSpec { PreferredNodes = ["studio", "tower"], SpreadGroup = "players" },
    };

    [Fact]
    public void Each_workload_lands_on_the_machine_that_meets_its_needs()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower, Lab.Edge);

        // Training needs CUDA and a lent accelerator: only the workstation qualifies.
        lab.Apply(Trainer()).NodeId.ShouldBe("tower");

        // Players prefer the desktop, which carries a measured two; the rest overflow to the workstation.
        foreach (var name in new[] { "player-1", "player-2", "player-3", "player-4" }) lab.Apply(Player(name));
        new[] { "player-1", "player-2", "player-3", "player-4" }.Select(n => lab.Workload(n).NodeId)
            .ShouldBe(["studio", "studio", "tower", "tower"]);

        // The edge box is the only one that allows the proxy program.
        lab.Apply(Lab.Program("proxy", "proxy")).NodeId.ShouldBe("edge");

        // A fifth player and a second trainer have nowhere to go, and the cluster says why for each node.
        var fifth = lab.Apply(Player("player-5"));
        fifth.Phase.ShouldBe(WorkloadPhase.Pending);
        fifth.Rejections["studio"].ShouldBe("player-slot: 1 requested, 0 free");
        fifth.Rejections["edge"].ShouldBe("capability 'program.player' required");

        var second = lab.Apply(Trainer("trainer-2"));
        second.NodeId.ShouldBeNull();
        second.Rejections["studio"].ShouldBe("capability 'cuda' required");
        second.Rejections["tower"].ShouldBe("gpu: 1 requested, 0 free");

        lab.Node("tower").Allocated["gpu"].ShouldBe(1);
        lab.Node("tower").Free["cpu"].ShouldBe(16 - 8 - 2 - 2);
    }

    [Fact]
    public void A_strict_limit_is_placed_only_where_it_can_be_enforced()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var limited = Lab.Program("capped", "player") with
        {
            Placement = new PlacementSpec { PreferredNodes = ["studio"] },
            Execution = new ExecutionProfile { Limits = new ExecutionLimits { MemoryMb = 4096 } },
        };

        // The desktop is preferred but cannot cap a process tree's memory, so the preference loses.
        var placed = lab.Apply(limited);
        placed.NodeId.ShouldBe("tower");
        placed.Rejections["studio"].ShouldBe("capability 'os.memory-limit' required");

        // Opting out of strictness is explicit, and then the preference applies.
        lab.Apply(limited with { Name = "best-effort-cap", Execution = limited.Execution with { Strict = false } }).NodeId.ShouldBe("studio");
    }

    [Fact]
    public void Batch_work_is_not_started_blind_but_interactive_work_still_is()
    {
        var lab = new Lab();
        lab.Controller.RegisterLocalNode(Lab.Tower);
        lab.Sync(Lab.Tower, "first", pressure: null); // Reachable, but it sent no reading.

        var batch = lab.Apply(Trainer());
        batch.NodeId.ShouldBeNull();
        batch.Rejections["tower"].ShouldBe("no fresh reading from the node; batch work is not started blind");

        lab.Apply(Player("player-1")).NodeId.ShouldBe("tower");

        // A reading that says "unknown" is not a clear reading either; a normal one finally admits the batch.
        lab.Sync(Lab.Tower, "first", PressureLevel.Normal);
        lab.Workload("trainer").NodeId.ShouldBe("tower");
    }

    [Fact]
    public void Pressure_needs_three_bad_readings_to_set_and_six_good_ones_to_clear()
    {
        var lab = new Lab();
        lab.Join(Lab.Tower);

        lab.Sync(Lab.Tower, "first", PressureLevel.Elevated);
        lab.Sync(Lab.Tower, "first", PressureLevel.Critical);
        lab.Node("tower").Pressured.ShouldBeFalse();
        lab.Sync(Lab.Tower, "first", PressureLevel.Elevated);
        lab.Node("tower").Pressured.ShouldBeTrue();
        lab.Apply(Trainer()).Rejections["tower"].ShouldBe("node is under pressure");

        for (var i = 0; i < 5; i++) lab.Sync(Lab.Tower, "first", PressureLevel.Normal);
        lab.Node("tower").Pressured.ShouldBeTrue();
        lab.Workload("trainer").NodeId.ShouldBeNull();

        lab.Sync(Lab.Tower, "first", PressureLevel.Normal);
        lab.Node("tower").Pressured.ShouldBeFalse();
        lab.Workload("trainer").NodeId.ShouldBe("tower");
    }

    [Fact]
    public void A_lost_node_keeps_pinned_work_and_gives_up_movable_work_after_the_grace()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var pinned = lab.Apply(Player("pinned") with { Placement = new PlacementSpec { PreferredNodes = ["studio"] } });
        var movable = lab.Apply(Player("movable") with
        {
            Placement = new PlacementSpec { PreferredNodes = ["studio"] },
            Lifecycle = new WorkloadLifecycle { OnNodeLoss = NodeLossPolicy.Reschedule },
        });
        lab.Sync(Lab.Studio, lab.Running(pinned), lab.Running(movable));
        lab.Workload("movable").Phase.ShouldBe(WorkloadPhase.Running);

        // The desktop goes silent; the workstation keeps syncing.
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        lab.Sync(Lab.Tower);
        lab.Node("studio").Reachable.ShouldBeFalse();
        lab.Workload("pinned").Phase.ShouldBe(WorkloadPhase.Unknown);
        lab.Workload("movable").NodeId.ShouldBe("studio");

        lab.Time.Advance(TimeSpan.FromSeconds(70));
        lab.Sync(Lab.Tower);
        lab.Workload("movable").NodeId.ShouldBe("tower");
        lab.Workload("movable").Generation.ShouldBe(movable.Generation + 1);

        // Pinned work waits for its node however long that takes, and is never reported as stopped.
        lab.Time.Advance(TimeSpan.FromHours(6));
        lab.Sync(Lab.Tower);
        lab.Workload("pinned").NodeId.ShouldBe("studio");
        lab.Workload("pinned").Phase.ShouldBe(WorkloadPhase.Unknown);

        // When the desktop returns it is told to run only what is still its own.
        lab.Sync(Lab.Studio, lab.Running(pinned)).Assignments.Select(a => a.Spec.Name).ShouldBe(["pinned"]);
    }

    [Fact]
    public void An_exclusive_key_moves_only_after_its_old_node_confirms_the_work_stopped()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var account = lab.Apply(Player("account") with
        {
            ExclusiveKey = "account/one",
            Placement = new PlacementSpec { PreferredNodes = ["studio"] },
            Lifecycle = new WorkloadLifecycle { OnNodeLoss = NodeLossPolicy.Reschedule },
        });
        account.NodeId.ShouldBe("studio");
        lab.Sync(Lab.Studio, lab.Running(account));

        // A second workload asking for the same key waits, and says who has it.
        lab.Apply(Player("rival") with { ExclusiveKey = "account/one" }).Reason.ShouldBe("exclusive key 'account/one' is held by workload 'account'");
        lab.Controller.Delete("rival").ShouldBeTrue();

        lab.Controller.SetAvailability("studio", NodeAvailability.Draining).ShouldBeTrue();
        lab.Workload("account").NodeId.ShouldBeNull();
        lab.Workload("account").Reason.ShouldBe("exclusive key 'account/one' is held by 'account', which node studio has not confirmed stopped");

        // The desktop is no longer told to run it, but while it still reports it alive the key stays put.
        lab.Sync(Lab.Studio, lab.Running(account)).Assignments.ShouldBeEmpty();
        lab.Sync(Lab.Studio, new WorkloadReport("account", account.Generation, WorkloadPhase.Stopping, lab.Time.GetUtcNow()));
        lab.Workload("account").NodeId.ShouldBeNull();

        lab.Sync(Lab.Studio, new WorkloadReport("account", account.Generation, WorkloadPhase.Stopped, lab.Time.GetUtcNow()));
        lab.Workload("account").NodeId.ShouldBe("tower");
        lab.Workload("account").Generation.ShouldBe(account.Generation + 1);
    }

    [Fact]
    public void An_exclusive_key_on_a_silent_node_is_released_only_after_the_node_must_have_fenced_itself()
    {
        var lab = new Lab(o => o.Controller.NodeLossGraceSeconds = 30);
        lab.Join(Lab.Studio, Lab.Tower);
        var account = lab.Apply(Player("account") with
        {
            ExclusiveKey = "account/one",
            Placement = new PlacementSpec { PreferredNodes = ["studio"] },
            Lifecycle = new WorkloadLifecycle { OnNodeLoss = NodeLossPolicy.Reschedule },
        });
        lab.Sync(Lab.Studio, lab.Running(account));

        // The node starts stopping such work 30 s after its last contact and the work may take its 30 s
        // grace, plus a few seconds to be ended if it ignores the request. The controller waits for all of
        // that and a second time-to-live on top (2 x 30 + 30 + 5 = 95 s), whatever the shorter loss grace says.
        lab.Time.Advance(TimeSpan.FromSeconds(45));
        lab.Sync(Lab.Tower);
        lab.Workload("account").NodeId.ShouldBe("studio");

        lab.Time.Advance(TimeSpan.FromSeconds(49));
        lab.Sync(Lab.Tower);
        lab.Workload("account").NodeId.ShouldBe("studio");

        lab.Time.Advance(TimeSpan.FromSeconds(2));
        lab.Sync(Lab.Tower);
        lab.Sync(Lab.Tower);
        lab.Workload("account").NodeId.ShouldBe("tower");
    }

    [Fact]
    public void Draining_moves_only_work_that_may_move_and_only_when_it_has_somewhere_to_go()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var stays = lab.Apply(Player("stays") with { Placement = new PlacementSpec { Nodes = ["studio"] } });
        var stuck = lab.Apply(Lab.Program("stuck", "trainer") with
        {
            Placement = new PlacementSpec { RequiredCapabilities = ["mps"] },
            Lifecycle = new WorkloadLifecycle { OnNodeLoss = NodeLossPolicy.Reschedule },
        });
        var moves = lab.Apply(Player("moves") with
        {
            Placement = new PlacementSpec { PreferredNodes = ["studio"] },
            Lifecycle = new WorkloadLifecycle { OnNodeLoss = NodeLossPolicy.Reschedule },
        });
        lab.Sync(Lab.Studio, lab.Running(stays), lab.Running(stuck), lab.Running(moves));

        lab.Controller.SetAvailability("studio", NodeAvailability.Draining);
        lab.Workload("stays").NodeId.ShouldBe("studio");   // Pinned: runs to completion where it is.
        lab.Workload("stuck").NodeId.ShouldBe("studio");   // Movable, but no other node has what it needs.
        lab.Workload("moves").NodeId.ShouldBe("tower");

        // Nothing new is placed on a draining or cordoned node; activating it takes new work again.
        lab.Apply(Player("late") with { Placement = new PlacementSpec { Nodes = ["studio"] } }).Rejections["studio"].ShouldBe("node is draining");
        lab.Controller.SetAvailability("studio", NodeAvailability.Cordoned);
        lab.Workload("late").Rejections["studio"].ShouldBe("node is cordoned");
        lab.Controller.SetAvailability("studio", NodeAvailability.Active);

        // The slot "moves" held is still counted until the desktop confirms it stopped it.
        lab.Workload("late").Rejections["studio"].ShouldBe("player-slot: 1 requested, 0 free");
        lab.Sync(Lab.Studio, lab.Running(stays), lab.Running(stuck));
        lab.Workload("late").NodeId.ShouldBe("studio");
    }

    [Fact]
    public void A_node_that_refuses_work_is_not_offered_it_again_until_the_specification_changes()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var player = lab.Apply(Player("player"));
        player.NodeId.ShouldBe("studio");

        lab.Sync(Lab.Studio, new WorkloadReport("player", player.Generation, WorkloadPhase.Refused, lab.Time.GetUtcNow(), "priority 'interactive' is above this node's maximum 'normal'"));
        var moved = lab.Workload("player");
        moved.NodeId.ShouldBe("tower");
        moved.Rejections["studio"].ShouldBe("the node's own policy refused this workload");

        // A changed specification is a new question, so the first choice is asked again.
        lab.Sync(Lab.Tower, new WorkloadReport("player", moved.Generation, WorkloadPhase.Refused, lab.Time.GetUtcNow(), "no"));
        lab.Workload("player").NodeId.ShouldBeNull();
        lab.Apply(Player("player") with { Class = WorkloadClass.Service }).NodeId.ShouldBe("studio");
    }

    [Fact]
    public void Reports_from_an_earlier_generation_and_from_a_replaced_process_are_not_believed()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio);
        var first = lab.Apply(Player("player"));
        lab.Sync(Lab.Studio, lab.Running(first));

        // Changing the spec keeps the workload on its node under a new generation.
        var second = lab.Apply(Player("player") with { Class = WorkloadClass.Service });
        second.NodeId.ShouldBe("studio");
        second.Generation.ShouldBe(first.Generation + 1);
        second.Phase.ShouldBe(WorkloadPhase.Assigned);

        // The node still reporting the old generation as failed says nothing about the new one.
        lab.Sync(Lab.Studio, new WorkloadReport("player", first.Generation, WorkloadPhase.Failed, lab.Time.GetUtcNow()));
        lab.Workload("player").Phase.ShouldBe(WorkloadPhase.Assigned);

        // A new process takes over the node id; the old one is told to stand down from then on.
        lab.Sync(Lab.Studio, "second", PressureLevel.Normal);
        lab.Controller.Sync("studio", lab.Request(Lab.Studio, "first")).Result.ShouldBe(SyncResult.Superseded);
        lab.Controller.Sync("studio", lab.Request(Lab.Studio, "second")).Result.ShouldBe(SyncResult.Ok);
        lab.Controller.Sync("ghost", lab.Request(Lab.Studio with { NodeId = "ghost" }, "x")).Result.ShouldBe(SyncResult.UnknownNode);
    }

    [Fact]
    public void Changing_an_exclusive_key_is_a_new_claim_and_the_old_key_stays_held_until_confirmed()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var holder = lab.Apply(Player("holder") with { ExclusiveKey = "account/one", Placement = new PlacementSpec { Nodes = ["studio"] } });
        var other = lab.Apply(Player("other") with { Placement = new PlacementSpec { Nodes = ["tower"] } });
        lab.Sync(Lab.Studio, lab.Running(holder));
        lab.Sync(Lab.Tower, lab.Running(other));

        // Giving a running workload a key somebody else holds must not leave two holders running.
        var claimed = lab.Apply(Player("other") with { ExclusiveKey = "account/one", Placement = new PlacementSpec { Nodes = ["tower"] } });
        claimed.NodeId.ShouldBeNull();
        claimed.Reason.ShouldBe("exclusive key 'account/one' is held by workload 'holder'");
        lab.Sync(Lab.Tower, lab.Running(other)).Assignments.ShouldBeEmpty(); // The keyless generation is told to stop.

        // The holder moves to another key. Its old generation may still be running out its grace, so the
        // old key is not free for the waiting workload until the node says that generation is gone.
        var moved = lab.Apply(Player("holder") with { ExclusiveKey = "account/two", Placement = new PlacementSpec { Nodes = ["studio"] } });
        moved.NodeId.ShouldBe("studio");
        moved.Generation.ShouldBeGreaterThan(holder.Generation);
        lab.Workload("other").Reason.ShouldBe("exclusive key 'account/one' is held by 'holder', which node studio has not confirmed stopped");

        lab.Sync(Lab.Studio, new WorkloadReport("holder", holder.Generation, WorkloadPhase.Stopping, lab.Time.GetUtcNow()));
        lab.Workload("other").NodeId.ShouldBeNull();
        lab.Sync(Lab.Tower);
        lab.Sync(Lab.Studio, lab.Running(moved));
        lab.Workload("other").NodeId.ShouldBe("tower");
    }

    [Fact]
    public void A_workload_created_again_under_the_same_name_never_reuses_a_generation()
    {
        var lab = new Lab();
        lab.Join(Lab.Tower);
        var first = lab.Apply(Trainer());
        lab.Sync(Lab.Tower, lab.Running(first));

        lab.Controller.Delete("trainer").ShouldBeTrue();
        var again = lab.Apply(Trainer() with { Requests = new Dictionary<string, decimal> { ["cpu"] = 1 } });
        again.Generation.ShouldBeGreaterThan(first.Generation);

        // The node still reporting the deleted one as running is not mistaken for the new one, and the
        // deleted one's accelerator is released only when the node stops reporting it.
        lab.Sync(Lab.Tower, lab.Running(first));
        lab.Workload("trainer").Phase.ShouldBe(WorkloadPhase.Assigned);
        lab.Node("tower").Allocated["cpu"].ShouldBe(9);
        lab.Sync(Lab.Tower, lab.Running(again));
        lab.Workload("trainer").Phase.ShouldBe(WorkloadPhase.Running);
        lab.Node("tower").Allocated["cpu"].ShouldBe(1);
    }

    [Fact]
    public void Editing_work_does_not_uproot_it_because_its_node_is_quiet_or_cordoned()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        var pinned = lab.Apply(Player("pinned"));
        pinned.NodeId.ShouldBe("studio");
        lab.Sync(Lab.Studio, lab.Running(pinned));

        lab.Controller.SetAvailability("studio", NodeAvailability.Cordoned);
        lab.Time.Advance(TimeSpan.FromMinutes(5));
        lab.Sync(Lab.Tower);
        lab.Node("studio").Reachable.ShouldBeFalse();

        var edited = lab.Apply(Player("pinned") with { Lifecycle = new WorkloadLifecycle { StopGrace = TimeSpan.FromMinutes(20) } });
        edited.NodeId.ShouldBe("studio");
        edited.Generation.ShouldBeGreaterThan(pinned.Generation);

        // A change the node really cannot satisfy does move it.
        lab.Apply(Player("pinned") with { Placement = new PlacementSpec { RequiredCapabilities = ["cuda"] } }).NodeId.ShouldBe("tower");
    }

    [Fact]
    public void Requests_are_applied_in_order_and_only_by_the_controller_process_they_were_meant_for()
    {
        var lab = new Lab(o => o.StateDirectory = _root);
        lab.Join(Lab.Studio);
        var player = lab.Apply(Player("player"));
        lab.Sync(Lab.Studio, lab.Running(player));

        // An older request delivered late says the node runs nothing. Believing it would confirm a stop
        // that never happened.
        var late = lab.Request(Lab.Studio);
        lab.Sync(Lab.Studio, lab.Running(player));
        lab.Controller.Sync("studio", late).Result.ShouldBe(SyncResult.Stale);
        lab.Workload("player").Phase.ShouldBe(WorkloadPhase.Running);

        // After a restart the controller remembers no nonces and no sequences, so a request captured before
        // it is refused for naming the previous process, whichever node process sent it.
        var captured = lab.Request(Lab.Studio, "an-older-node-process");
        lab.RestartController();
        lab.Controller.Sync("studio", captured).Result.ShouldBe(SyncResult.WrongController);
        lab.Controller.Sync("studio", lab.Request(Lab.Studio)).Result.ShouldBe(SyncResult.Ok);
    }

    [Fact]
    public void Deleting_a_workload_frees_its_capacity_once_the_node_confirms()
    {
        var lab = new Lab();
        lab.Join(Lab.Tower);
        var trainer = lab.Apply(Trainer());
        lab.Sync(Lab.Tower, lab.Running(trainer));
        lab.Controller.Delete("trainer").ShouldBeTrue();

        // Still running there as far as anyone knows, so the accelerator is not free yet.
        lab.Apply(Trainer("next")).Rejections["tower"].ShouldBe("gpu: 1 requested, 0 free");
        lab.Sync(Lab.Tower, lab.Running(trainer)).Assignments.ShouldBeEmpty();
        lab.Workload("next").NodeId.ShouldBeNull();

        lab.Sync(Lab.Tower);
        lab.Workload("next").NodeId.ShouldBe("tower");
    }

    [Fact]
    public void Contributed_rules_can_remove_and_rank_nodes_but_cannot_override_the_kernels_own()
    {
        var lab = new Lab(filters: [new NoTower()], scorers: [new LovesEdge()]);
        lab.Join(Lab.Studio, Lab.Tower, Lab.Edge);

        var player = lab.Apply(Player("player") with { Placement = new PlacementSpec { PreferredNodes = ["tower", "studio"] } });
        player.NodeId.ShouldBe("studio");
        player.Rejections["tower"].ShouldBe("no-tower: house rule");

        // The scorer adores the edge box, which lacks the program: a score cannot make a node eligible.
        player.Rejections["edge"].ShouldBe("capability 'program.player' required");
    }

    private sealed class NoTower : IPlacementFilter
    {
        public string Id => "no-tower";
        public string? Reject(WorkloadPlacementRequest request, NodeView node) => node.Descriptor.NodeId == "tower" ? "house rule" : null;
    }

    private sealed class LovesEdge : IPlacementScorer
    {
        public string Id => "loves-edge";
        public decimal Score(WorkloadPlacementRequest request, NodeView node) => node.Descriptor.NodeId == "edge" ? 1_000_000_000m : 0;
    }

    [Fact]
    public void Invalid_specifications_are_declined_with_reasons()
    {
        var lab = new Lab();
        lab.Controller.Apply(Lab.Program("Bad Name", "player")).Errors.ShouldContain(e => e.StartsWith("name must be"));
        lab.Controller.Apply(new WorkloadSpec { Name = "no-program" }).Errors.ShouldBe(["payload.program must name a program"]);
        lab.Controller.Apply(Lab.Program("negative", "player") with { Requests = new Dictionary<string, decimal> { ["cpu"] = -1 } })
            .Errors.ShouldBe(["requests must be named and not negative"]);

        // A kind the controller has no driver for is accepted unvalidated; a node that has the driver decides.
        var custom = lab.Controller.Apply(new WorkloadSpec { Name = "custom", Kind = "lab.custom" });
        custom.Accepted.ShouldBeTrue();
        custom.Workload!.Reason.ShouldBe("the cluster has no nodes");
    }

    [Fact]
    public void Membership_and_placements_survive_a_controller_restart()
    {
        var lab = new Lab(o => o.StateDirectory = _root);
        lab.Join(Lab.Studio, Lab.Tower);
        var player = lab.Apply(Player("player"));
        var identity = lab.Controller.Identity;
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(_root, "controller.json")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        lab.RestartController();
        lab.Controller.Identity.ClusterId.ShouldBe(identity.ClusterId);
        lab.Controller.Identity.ControllerInstanceId.ShouldNotBe(identity.ControllerInstanceId);

        // The placement is remembered, but what the node is doing is not assumed: unknown until it reports.
        var restored = lab.Workload("player");
        restored.NodeId.ShouldBe("studio");
        restored.Generation.ShouldBe(player.Generation);
        restored.Phase.ShouldBe(WorkloadPhase.Unknown);
        lab.Node("studio").Reachable.ShouldBeFalse();

        lab.Sync(Lab.Studio, lab.Running(restored));
        lab.Workload("player").Phase.ShouldBe(WorkloadPhase.Running);
    }
}
