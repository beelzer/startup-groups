using Salvo.App.ViewModels;
using Salvo.App.ViewModels.Flow;
using Salvo.Core.Models;
using Salvo.Core.Models.Flow;

namespace Salvo.App.Tests;

/// <summary>
/// Locks the Simple-view branch authoring transform: the If node holds its
/// then/else child sequences on the view-model, but they must compile down
/// to (and reconstruct from) the flat then/else-labeled edge graph that the
/// orchestrator executes. These tests pin both directions and their round
/// trip so the on-disk model stays orchestrator-compatible.
/// </summary>
public sealed class FlowBranchRoundTripTests
{
    // Start → If ─then→ R(then cmd) ─┐
    //             ─else→ W(else wait) ┴→ App(join)
    // Every branch edge (interior + join) carries the branch label — the
    // exact shape GroupGraphViewModel.WriteTo emits.
    private static Group BuildFlatGroup() => new()
    {
        Id = "g",
        Nodes =
        [
            new StartNode { Id = "s" },
            new IfElseNode { Id = "if", Condition = new ServiceRunningCondition { ServiceName = "spooler" } },
            new RunCommandNode { Id = "t0", Command = "echo then" },
            new WaitNode { Id = "e0", DurationSeconds = 3 },
            new AppNode { Id = "j", App = new AppEntry { Name = "joinapp" } },
        ],
        Edges =
        [
            new Edge { Id = "x1", From = "s", To = "if" },
            new Edge { Id = "x2", From = "if", To = "t0", Label = "then" },
            new Edge { Id = "x3", From = "t0", To = "j", Label = "then" },
            new Edge { Id = "x4", From = "if", To = "e0", Label = "else" },
            new Edge { Id = "x5", From = "e0", To = "j", Label = "else" },
        ],
    };

    [Fact]
    public void FromModel_LiftsBranchNodesOntoIfNode()
    {
        var vm = GroupGraphViewModel.FromModel(BuildFlatGroup());

        // Branch nodes are lifted out of the outer graph.
        vm.Nodes.Select(n => n.Id).Should().BeEquivalentTo("s", "if", "j");

        var ifVm = vm.Nodes.OfType<IfElseNodeViewModel>().Single();
        ifVm.ThenNodes.Select(n => n.Id).Should().Equal("t0");
        ifVm.ElseNodes.Select(n => n.Id).Should().Equal("e0");

        // Outer edges collapse to Start→If→Join with no labels leaking.
        vm.Edges.Should().Contain(e => e.From == "s" && e.To == "if");
        vm.Edges.Should().ContainSingle(e => e.From == "if")
            .Which.Should().Match<EdgeViewModel>(e => e.To == "j" && string.IsNullOrEmpty(e.Label));
        vm.Edges.Should().NotContain(e => e.Label == "then" || e.Label == "else");
    }

    [Fact]
    public void WriteTo_ExpandsBranchesIntoLabeledEdges()
    {
        var vm = GroupGraphViewModel.FromModel(BuildFlatGroup());

        var flat = new Group { Id = "g2" };
        vm.WriteTo(flat);

        flat.Nodes.Select(n => n.Id).Should().BeEquivalentTo("s", "if", "t0", "e0", "j");

        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "t0" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "t0" && e.To == "j" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "e0" && e.Label == "else");
        flat.Edges.Should().Contain(e => e.From == "e0" && e.To == "j" && e.Label == "else");
        flat.Edges.Should().Contain(e => e.From == "s" && e.To == "if" && string.IsNullOrEmpty(e.Label));

        // The opaque outer If→join edge must not survive into the flat model.
        flat.Edges.Should().NotContain(e => e.From == "if" && e.To == "j" && string.IsNullOrEmpty(e.Label));
    }

    [Fact]
    public void RoundTrip_IsStable()
    {
        var vm1 = GroupGraphViewModel.FromModel(BuildFlatGroup());
        var mid = new Group { Id = "m" };
        vm1.WriteTo(mid);
        var vm2 = GroupGraphViewModel.FromModel(mid);

        vm2.Nodes.Select(n => n.Id).Should().BeEquivalentTo("s", "if", "j");
        var ifVm = vm2.Nodes.OfType<IfElseNodeViewModel>().Single();
        ifVm.ThenNodes.Select(n => n.Id).Should().Equal("t0");
        ifVm.ElseNodes.Select(n => n.Id).Should().Equal("e0");
        ifVm.Condition.Should().BeOfType<ServiceRunningConditionViewModel>()
            .Which.ServiceName.Should().Be("spooler");
    }

    [Fact]
    public void EmptyBranches_WithJoin_RoundTrip()
    {
        // Start → If ─then→ J, If ─else→ J  (both branches empty, reconverge)
        var group = new Group
        {
            Id = "g",
            Nodes =
            [
                new StartNode { Id = "s" },
                new IfElseNode { Id = "if", Condition = new ServiceRunningCondition { ServiceName = "x" } },
                new AppNode { Id = "j", App = new AppEntry { Name = "j" } },
            ],
            Edges =
            [
                new Edge { Id = "x1", From = "s", To = "if" },
                new Edge { Id = "x2", From = "if", To = "j", Label = "then" },
                new Edge { Id = "x3", From = "if", To = "j", Label = "else" },
            ],
        };

        var vm = GroupGraphViewModel.FromModel(group);
        var ifVm = vm.Nodes.OfType<IfElseNodeViewModel>().Single();
        ifVm.ThenNodes.Should().BeEmpty();
        ifVm.ElseNodes.Should().BeEmpty();
        vm.Edges.Should().ContainSingle(e => e.From == "if").Which.To.Should().Be("j");

        var flat = new Group { Id = "o" };
        vm.WriteTo(flat);
        flat.Edges.Where(e => e.From == "if").Should().HaveCount(2);
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "j" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "j" && e.Label == "else");
    }

    [Fact]
    public void GroupViewModel_FromModel_CollapsesBranches_OnRealLoadPath()
    {
        // The app loads groups through GroupViewModel.FromModel, which builds
        // the graph directly — this pins that it also runs the branch
        // collapse (the round-trip would otherwise break in the real app).
        var gvm = Salvo.App.ViewModels.GroupViewModel.FromModel(BuildFlatGroup());

        gvm.Graph.Nodes.Select(n => n.Id).Should().BeEquivalentTo("s", "if", "j");
        var ifVm = gvm.Graph.Nodes.OfType<IfElseNodeViewModel>().Single();
        ifVm.ThenNodes.Select(n => n.Id).Should().Equal("t0");
        ifVm.ElseNodes.Select(n => n.Id).Should().Equal("e0");

        var saved = gvm.ToModel();
        saved.Edges.Should().Contain(e => e.From == "if" && e.To == "t0" && e.Label == "then");
        saved.Edges.Should().Contain(e => e.From == "e0" && e.To == "j" && e.Label == "else");
    }

    [Fact]
    public void MovingOuterNodeIntoThenBranch_ExpandsToLabeledChain()
    {
        // Start → A → If → B   (B is the join)
        var group = new Group
        {
            Id = "g",
            Nodes =
            [
                new StartNode { Id = "s" },
                new AppNode { Id = "a", App = new AppEntry { Name = "a" } },
                new IfElseNode { Id = "if", Condition = new ServiceRunningCondition() },
                new AppNode { Id = "b", App = new AppEntry { Name = "b" } },
            ],
            Edges =
            [
                new Edge { Id = "x1", From = "s", To = "a" },
                new Edge { Id = "x2", From = "a", To = "if" },
                new Edge { Id = "x3", From = "if", To = "b" },
            ],
        };

        var vm = GroupGraphViewModel.FromModel(group);
        var ifVm = vm.Nodes.OfType<IfElseNodeViewModel>().Single();
        var a = vm.Nodes.Single(n => n.Id == "a");

        // What MoveNodeIntoBranch does: detach from outer, append to branch.
        vm.RemoveNode(a);
        ifVm.ThenNodes.Add(a);

        var flat = new Group { Id = "o" };
        vm.WriteTo(flat);

        // Outer chain heals to Start → If; then-branch runs A before the
        // join B; empty else jumps straight to the join.
        flat.Edges.Should().Contain(e => e.From == "s" && e.To == "if" && string.IsNullOrEmpty(e.Label));
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "a" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "a" && e.To == "b" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "b" && e.Label == "else");
    }

    // Start → If → J with then = [A], else = [].
    private static Group BuildIfWithThenNode() => new()
    {
        Id = "g",
        Nodes =
        [
            new StartNode { Id = "s" },
            new IfElseNode { Id = "if", Condition = new ServiceRunningCondition() },
            new AppNode { Id = "a", App = new AppEntry { Name = "a" } },
            new AppNode { Id = "j", App = new AppEntry { Name = "j" } },
        ],
        Edges =
        [
            new Edge { Id = "1", From = "s", To = "if" },
            new Edge { Id = "2", From = "if", To = "a", Label = "then" },
            new Edge { Id = "3", From = "a", To = "j", Label = "then" },
            new Edge { Id = "4", From = "if", To = "j", Label = "else" },
        ],
    };

    [Fact]
    public void MovingNodeAcrossBranches_FlipsTheLabel()
    {
        var vm = GroupGraphViewModel.FromModel(BuildIfWithThenNode());
        var ifVm = vm.Nodes.OfType<IfElseNodeViewModel>().Single();
        var a = ifVm.ThenNodes.Single();

        // What MoveNodeIntoBranch does for a branch→branch move.
        ifVm.ThenNodes.Remove(a);
        ifVm.ElseNodes.Add(a);

        var flat = new Group { Id = "o" };
        vm.WriteTo(flat);

        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "a" && e.Label == "else");
        flat.Edges.Should().Contain(e => e.From == "a" && e.To == "j" && e.Label == "else");
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "j" && e.Label == "then");
        flat.Edges.Should().NotContain(e => e.To == "a" && e.Label == "then");
    }

    [Fact]
    public void ExtractingBranchNodeToOuter_MakesItAPlainOuterNode()
    {
        var vm = GroupGraphViewModel.FromModel(BuildIfWithThenNode());
        var ifVm = vm.Nodes.OfType<IfElseNodeViewModel>().Single();
        var a = ifVm.ThenNodes.Single();
        var jStage = vm.Stages.First(s => s.Nodes.Any(n => n.Id == "j"));

        // What ExtractBranchNodeToOuter + the drop positioning do.
        ifVm.ThenNodes.Remove(a);
        vm.Nodes.Add(a);
        vm.MoveNodeAfterStage(a, jStage);

        ifVm.ThenNodes.Should().BeEmpty();

        var flat = new Group { Id = "o" };
        vm.WriteTo(flat);

        // A is a normal outer node now — reached by a plain edge, never a
        // then/else-labeled one.
        flat.Edges.Should().Contain(e => e.From == "j" && e.To == "a" && string.IsNullOrEmpty(e.Label));
        flat.Edges.Should().NotContain(e => e.To == "a" && (e.Label == "then" || e.Label == "else"));
    }

    [Fact]
    public void MultiSuccessorIf_CreatedByMergeIntoStage_RoundTripsWithoutCorruption()
    {
        // Start → If → A → B with then = [t0]. Merging B into A's stage
        // makes the If a predecessor of BOTH A and B — two unlabeled
        // out-edges from one If, a shape the editor itself creates. The
        // old per-edge expansion emitted the branch chains twice and the
        // reload disconnected one successor next to START.
        var group = new Group
        {
            Id = "g",
            Nodes =
            [
                new StartNode { Id = "s" },
                new IfElseNode { Id = "if", Condition = new ServiceRunningCondition { ServiceName = "svc" } },
                new RunCommandNode { Id = "t0", Command = "echo then" },
                new AppNode { Id = "a", App = new AppEntry { Name = "a" } },
                new AppNode { Id = "b", App = new AppEntry { Name = "b" } },
            ],
            Edges =
            [
                new Edge { Id = "1", From = "s", To = "if" },
                new Edge { Id = "2", From = "if", To = "t0", Label = "then" },
                new Edge { Id = "3", From = "t0", To = "a", Label = "then" },
                new Edge { Id = "4", From = "if", To = "a", Label = "else" },
                new Edge { Id = "5", From = "a", To = "b" },
            ],
        };

        var vm = GroupGraphViewModel.FromModel(group);
        var b = vm.Nodes.Single(n => n.Id == "b");
        var aStage = vm.Stages.Single(s => s.Nodes.Any(n => n.Id == "a"));
        vm.MergeIntoStage(b, aStage);

        vm.Edges.Where(e => e.From == "if").Select(e => e.To)
            .Should().BeEquivalentTo(["a", "b"], "the merge makes the If fan out to both parallel siblings");

        // Save: the branch chain must be expanded exactly once, with the
        // tail fanning out to every join — all labeled.
        var flat = new Group { Id = "m" };
        vm.WriteTo(flat);
        flat.Edges.Where(e => e.From == "if" && e.To == "t0" && e.Label == "then").Should().HaveCount(1);
        flat.Edges.Should().Contain(e => e.From == "t0" && e.To == "a" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "t0" && e.To == "b" && e.Label == "then");
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "a" && e.Label == "else");
        flat.Edges.Should().Contain(e => e.From == "if" && e.To == "b" && e.Label == "else");

        // Reload: both successors reattach to the If; nothing is orphaned.
        var vm2 = GroupGraphViewModel.FromModel(flat);
        vm2.Nodes.Select(n => n.Id).Should().BeEquivalentTo("s", "if", "a", "b");
        vm2.Nodes.OfType<IfElseNodeViewModel>().Single().ThenNodes.Select(n => n.Id).Should().Equal("t0");
        vm2.Edges.Where(e => e.From == "if").Select(e => e.To).Should().BeEquivalentTo(["a", "b"]);
        foreach (var node in vm2.Nodes.Where(n => n is not StartNodeViewModel))
        {
            vm2.Edges.Should().Contain(e => e.To == node.Id,
                $"node '{node.Id}' must stay reachable after the round trip");
        }
    }

    [Fact]
    public void GroupViewModel_Apps_IncludesBranchApps_AndTracksBranchEdits()
    {
        // Branch apps must feed the group-level aggregates (icons, running
        // dots, ApplyResults identity matching) exactly like outer apps.
        var gvm = Salvo.App.ViewModels.GroupViewModel.FromModel(BuildFlatGroup());

        gvm.Apps.Select(a => a.Name).Should().Contain("joinapp");
        gvm.HasApps.Should().BeTrue();

        var ifVm = gvm.Graph.Nodes.OfType<IfElseNodeViewModel>().Single();
        var appsChanged = 0;
        gvm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(gvm.Apps)) appsChanged++; };

        ifVm.ThenNodes.Add(new AppNodeViewModel
        {
            Id = "ba",
            App = AppEntryViewModel.FromModel(new AppEntry { Name = "branchapp", Path = @"C:\x\branch.exe" }),
        });

        gvm.Apps.Select(a => a.Name).Should().Contain(["joinapp", "branchapp"]);
        appsChanged.Should().BeGreaterThan(0, "adding a branch app must re-raise the aggregates");

        // Running-state changes on a branch app bubble into the group.
        var anyRunningRaised = 0;
        gvm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(gvm.AnyRunning)) anyRunningRaised++; };
        gvm.Apps.Single(a => a.Name == "branchapp").IsRunning = true;
        anyRunningRaised.Should().BeGreaterThan(0);
        gvm.AnyRunning.Should().BeTrue();
    }

    [Fact]
    public void LegacyIfNode_WithNoBranchEdges_LoadsAsEmptyBranches()
    {
        // Pre-branch groups spliced the If in linearly with a single
        // unlabeled out-edge. Those must load cleanly as an empty If.
        var group = new Group
        {
            Id = "g",
            Nodes =
            [
                new StartNode { Id = "s" },
                new IfElseNode { Id = "if", Condition = new ServiceRunningCondition() },
                new AppNode { Id = "j", App = new AppEntry { Name = "j" } },
            ],
            Edges =
            [
                new Edge { Id = "x1", From = "s", To = "if" },
                new Edge { Id = "x2", From = "if", To = "j" },
            ],
        };

        var vm = GroupGraphViewModel.FromModel(group);
        var ifVm = vm.Nodes.OfType<IfElseNodeViewModel>().Single();
        ifVm.ThenNodes.Should().BeEmpty();
        ifVm.ElseNodes.Should().BeEmpty();
        vm.Nodes.Select(n => n.Id).Should().BeEquivalentTo("s", "if", "j");
        vm.Edges.Should().Contain(e => e.From == "if" && e.To == "j");
    }
}
