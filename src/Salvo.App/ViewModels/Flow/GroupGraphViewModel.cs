using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Salvo.Core.Models;
using Salvo.Core.Models.Flow;

namespace Salvo.App.ViewModels.Flow;

/// <summary>
/// Editor view-model for a single group's flow graph. Owns the live
/// Nodes/Edges collections and the derived Stages that the Simple view
/// renders.
///
/// Authoring surface:
/// - <see cref="AddNodeAfter"/> / <see cref="AddNodeBetween"/> insert a
///   new node into the topology, wiring edges so the surrounding
///   structure stays valid.
/// - <see cref="RemoveNode"/> drops the node and rewires its in-edges
///   to its out-edges so the graph stays connected.
/// - <see cref="ReorderInStage"/> rewires edges to preserve stage
///   semantics during drag-reorder.
///
/// <see cref="Stages"/> recomputes on every structural change. Cheap for
/// typical graph sizes (&lt;100 nodes); we can index later if needed.
/// </summary>
public sealed partial class GroupGraphViewModel : ObservableObject
{
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
    public ObservableCollection<EdgeViewModel> Edges { get; } = [];

    /// <summary>
    /// Topologically grouped stages for the Simple view to render. Read-
    /// only from XAML's perspective; rebuilt internally on edit.
    /// </summary>
    public ObservableCollection<StageViewModel> Stages { get; } = [];

    /// <summary>
    /// True when there is nothing in the graph apart from an optional
    /// Start node. Drives the empty-state CTA in the view.
    /// </summary>
    public bool IsEmpty => Nodes.Count <= 1 && Nodes.All(n => n is StartNodeViewModel);

    public GroupGraphViewModel()
    {
        Nodes.CollectionChanged += OnNodesChanged;
        Edges.CollectionChanged += OnEdgesChanged;
    }

    public static GroupGraphViewModel FromModel(Group group)
    {
        var vm = new GroupGraphViewModel();

        // Tests / fresh groups may hand us a graph that's empty but with
        // a legacy Apps[] list; migrate so the editor always opens onto a
        // populated graph.
        if (group.Nodes.Count == 0 && group.Apps.Count > 0)
        {
            FlowMigration.Migrate(group);
        }

        // Brand-new empty group: seed a single Start node so the user
        // has something to anchor edits onto.
        if (group.Nodes.Count == 0)
        {
            group.Nodes.Add(new StartNode { Id = Guid.NewGuid().ToString() });
        }

        foreach (var n in group.Nodes)
        {
            vm.Nodes.Add(NodeViewModel.FromModel(n));
        }
        foreach (var e in group.Edges)
        {
            vm.Edges.Add(EdgeViewModel.FromModel(e));
        }
        vm.CollapseBranches();
        vm.RebuildStages();
        return vm;
    }

    public void WriteTo(Group group)
    {
        group.Nodes.Clear();
        group.Edges.Clear();

        // Emit every outer node, plus the branch payload hanging off each
        // If node (which the outer graph stores opaquely on the If VM).
        foreach (var n in Nodes)
        {
            group.Nodes.Add(n.ToModel());
            if (n is IfElseNodeViewModel ifVm)
            {
                foreach (var bn in ifVm.ThenNodes) group.Nodes.Add(bn.ToModel());
                foreach (var bn in ifVm.ElseNodes) group.Nodes.Add(bn.ToModel());
            }
        }

        // Expand each If's opaque outer edges (If → join(s)) into two
        // labeled branch chains that reconverge at every join, so the
        // orchestrator sees the flat then/else-labeled graph it executes.
        // The expansion is per-If, not per-edge: an If can have several
        // unlabeled successors (MergeIntoStage wires it as predecessor of
        // every node in the merged stage), and expanding once per edge
        // emitted duplicate branch chains that corrupted the next load.
        var joinsByIf = new Dictionary<string, List<string>>();
        foreach (var e in Edges)
        {
            if (NodeById(e.From) is IfElseNodeViewModel && string.IsNullOrEmpty(e.Label))
            {
                if (!joinsByIf.TryGetValue(e.From, out var joins))
                {
                    joinsByIf[e.From] = joins = [];
                }
                if (!joins.Contains(e.To)) joins.Add(e.To);
            }
            else
            {
                group.Edges.Add(e.ToModel());
            }
        }

        // Leaf If nodes (no outer outgoing edge) get an empty join list:
        // their branch chains simply terminate.
        foreach (var ifVm in Nodes.OfType<IfElseNodeViewModel>())
        {
            var joins = joinsByIf.GetValueOrDefault(ifVm.Id) ?? [];
            ExpandBranch(group, ifVm, NodeBranches.Then, ifVm.ThenNodes, joins);
            ExpandBranch(group, ifVm, NodeBranches.Else, ifVm.ElseNodes, joins);
        }
    }

    /// <summary>
    /// Emit the flat edge chain for one branch of an If node:
    /// <c>If -[label]-> b0 -[label]-> b1 ... -[label]-> join</c>. Every
    /// edge carries the branch label so the load-time
    /// <see cref="CollapseBranches"/> can invert it unambiguously; the
    /// orchestrator ignores the label on non-If sources (they fire all
    /// outgoing regardless), so labelling the interior edges is safe.
    /// With several joins the tail fans out to each of them (all labeled).
    /// An empty branch fans the If straight out to every join; an empty
    /// branch with no join emits nothing.
    /// </summary>
    private static void ExpandBranch(Group group, IfElseNodeViewModel ifVm, string label,
        IReadOnlyList<NodeViewModel> branchNodes, IReadOnlyList<string> joinIds)
    {
        var prevId = ifVm.Id;
        foreach (var bn in branchNodes)
        {
            group.Edges.Add(new Edge { Id = Guid.NewGuid().ToString(), From = prevId, To = bn.Id, Label = label });
            prevId = bn.Id;
        }
        foreach (var joinId in joinIds)
        {
            group.Edges.Add(new Edge { Id = Guid.NewGuid().ToString(), From = prevId, To = joinId, Label = label });
        }
    }

    /// <summary>
    /// Inverse of the <see cref="WriteTo"/> branch expansion. For each If
    /// node, lift its then/else-labeled edge chains out of the flat graph
    /// into the If VM's <see cref="IfElseNodeViewModel.ThenNodes"/> /
    /// <see cref="IfElseNodeViewModel.ElseNodes"/> collections, leaving the
    /// If as a single opaque node connected directly to the branches'
    /// join (if any). Runs once on load.
    /// </summary>
    public void CollapseBranches()
    {
        foreach (var ifVm in Nodes.OfType<IfElseNodeViewModel>().ToList())
        {
            var (thenPath, thenJoins) = FollowLabeledChain(ifVm.Id, NodeBranches.Then);
            var (elsePath, elseJoins) = FollowLabeledChain(ifVm.Id, NodeBranches.Else);

            List<string> joins;
            List<string> thenIds;
            List<string> elseIds;
            if (thenJoins.Count > 0 || elseJoins.Count > 0)
            {
                // Fan-out tail: the chain ended on a node with several
                // labeled out-edges — those targets are the joins (a
                // multi-successor If; both branches fan to the same set).
                joins = thenJoins.Union(elseJoins).ToList();
                thenIds = thenPath;
                elseIds = elsePath;
            }
            else
            {
                // Single-successor shape: if both chains end at the same
                // node, that node is the join and stays in the outer
                // graph. Otherwise the branches are leaves and every
                // chained node belongs to them.
                string? joinId = thenPath.Count > 0 && elsePath.Count > 0 && thenPath[^1] == elsePath[^1]
                    ? thenPath[^1]
                    : null;
                joins = joinId is null ? [] : [joinId];
                thenIds = joinId is null ? thenPath : thenPath.Take(thenPath.Count - 1).ToList();
                elseIds = joinId is null ? elsePath : elsePath.Take(elsePath.Count - 1).ToList();
            }

            MoveIntoBranch(ifVm.ThenNodes, thenIds);
            MoveIntoBranch(ifVm.ElseNodes, elseIds);

            // Drop the labeled edges that made up the branches, then
            // reconnect the If straight to each join so the outer graph
            // regains its opaque If → join edge(s).
            var branchIds = new HashSet<string>(thenIds.Concat(elseIds)) { ifVm.Id };
            foreach (var dead in Edges.Where(e =>
                         (e.Label == NodeBranches.Then || e.Label == NodeBranches.Else) && branchIds.Contains(e.From)).ToList())
            {
                Edges.Remove(dead);
            }
            foreach (var joinId in joins)
            {
                Edges.Add(new EdgeViewModel { Id = Guid.NewGuid().ToString(), From = ifVm.Id, To = joinId });
            }
        }
    }

    /// <summary>
    /// Walk a labelled chain starting at <paramref name="startId"/>,
    /// following the single out-edge carrying <paramref name="label"/> at
    /// each step. Returns the ordered list of visited target node ids,
    /// plus the join set when the walk hits a fan-out tail (a node with
    /// several out-edges of this label — the multi-join shape
    /// <see cref="ExpandBranch"/> emits; every target is a join, not a
    /// branch node). The guard bounds the walk to the node count so a
    /// malformed cyclic graph can't spin forever.
    /// </summary>
    private (List<string> Path, List<string> Joins) FollowLabeledChain(string startId, string label)
    {
        var path = new List<string>();
        var currentFrom = startId;
        for (var guard = 0; guard <= Nodes.Count; guard++)
        {
            var outs = Edges.Where(e => e.From == currentFrom && e.Label == label).ToList();
            if (outs.Count == 0) break;
            if (outs.Count > 1)
            {
                return (path, outs.Select(e => e.To).Distinct().ToList());
            }
            path.Add(outs[0].To);
            currentFrom = outs[0].To;
        }
        return (path, []);
    }

    private void MoveIntoBranch(ObservableCollection<NodeViewModel> branch, IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) continue;
            Nodes.Remove(node);
            branch.Add(node);
        }
    }

    private NodeViewModel? NodeById(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    // ---- Editing -----------------------------------------------------

    /// <summary>
    /// Insert <paramref name="newNode"/> after <paramref name="predecessor"/>
    /// in the topology. The predecessor's existing outgoing edges are
    /// rewired to leave the new node instead, preserving downstream
    /// structure.
    /// </summary>
    public void AddNodeAfter(NodeViewModel predecessor, NodeViewModel newNode)
    {
        if (string.IsNullOrEmpty(newNode.Id)) newNode.Id = Guid.NewGuid().ToString();
        Nodes.Add(newNode);

        var existing = Edges.Where(e => e.From == predecessor.Id).ToList();
        foreach (var e in existing)
        {
            e.From = newNode.Id;
        }

        Edges.Add(new EdgeViewModel
        {
            Id = Guid.NewGuid().ToString(),
            From = predecessor.Id,
            To = newNode.Id,
        });

        RebuildStages();
    }

    /// <summary>
    /// Insert a new node into the same stage as <paramref name="sibling"/>,
    /// so it runs in parallel with that sibling. Implemented by
    /// duplicating sibling's incoming edges to the new node and its
    /// outgoing edges from the new node.
    /// </summary>
    public void AddSiblingOf(NodeViewModel sibling, NodeViewModel newNode)
    {
        if (string.IsNullOrEmpty(newNode.Id)) newNode.Id = Guid.NewGuid().ToString();
        Nodes.Add(newNode);

        foreach (var inc in Edges.Where(e => e.To == sibling.Id).ToList())
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = inc.From,
                To = newNode.Id,
            });
        }

        foreach (var outg in Edges.Where(e => e.From == sibling.Id).ToList())
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = newNode.Id,
                To = outg.To,
            });
        }

        RebuildStages();
    }

    /// <summary>
    /// Remove a node and re-stitch the graph: every edge into the node
    /// gets re-targeted at every node the removed node pointed to.
    /// Concretely: drop the node, then for each (in, removed, out) chain
    /// create a direct (in, out) edge. Idempotent on the Start node
    /// (refuses to remove it).
    /// </summary>
    public void RemoveNode(NodeViewModel node)
    {
        if (node is StartNodeViewModel) return;

        var ins = Edges.Where(e => e.To == node.Id).Select(e => e.From).ToList();
        var outs = Edges.Where(e => e.From == node.Id).Select(e => e.To).ToList();

        var dropEdges = Edges.Where(e => e.From == node.Id || e.To == node.Id).ToList();
        foreach (var e in dropEdges) Edges.Remove(e);

        foreach (var inId in ins)
        {
            foreach (var outId in outs)
            {
                if (Edges.Any(x => x.From == inId && x.To == outId))
                    continue; // don't duplicate

                Edges.Add(new EdgeViewModel
                {
                    Id = Guid.NewGuid().ToString(),
                    From = inId,
                    To = outId,
                });
            }
        }

        Nodes.Remove(node);
        RebuildStages();
    }

    // ---- Stage computation ------------------------------------------

    /// <summary>
    /// Topological BFS into stages. Kahn's algorithm with a twist:
    /// instead of emitting nodes one at a time, we emit each "layer"
    /// (all nodes whose pending incoming count reached zero in this
    /// pass) as one stage. That gives the Simple view its parallel-row
    /// visualization for free.
    ///
    /// Public so external load paths can trigger a rebuild after they
    /// populate Nodes/Edges directly.
    /// </summary>
    public void RebuildStages()
    {
        var pending = Nodes.ToDictionary(n => n.Id, n => 0);
        foreach (var e in Edges)
        {
            if (pending.ContainsKey(e.To)) pending[e.To]++;
        }

        var nodeById = Nodes.ToDictionary(n => n.Id);
        var outgoing = Nodes.ToDictionary(n => n.Id, _ => new List<EdgeViewModel>());
        foreach (var e in Edges)
        {
            if (outgoing.TryGetValue(e.From, out var list)) list.Add(e);
        }

        var stages = new List<List<NodeViewModel>>();
        var ready = Nodes.Where(n => pending[n.Id] == 0).ToList();

        while (ready.Count > 0)
        {
            stages.Add(ready);

            var next = new List<NodeViewModel>();
            foreach (var n in ready)
            {
                foreach (var e in outgoing[n.Id])
                {
                    if (!pending.ContainsKey(e.To)) continue;
                    pending[e.To]--;
                    if (pending[e.To] == 0)
                    {
                        next.Add(nodeById[e.To]);
                    }
                }
            }
            ready = next;
        }

        // Reconcile into the observable collection without rebuilding it
        // wholesale — keeps WPF item containers stable across edits.
        Stages.Clear();
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = new StageViewModel { Index = i };
            foreach (var node in stages[i]) stage.Nodes.Add(node);
            Stages.Add(stage);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnEdgesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Stages already get rebuilt by the methods that move edges; no
        // automatic rebuild on raw collection edits to avoid double work.
    }

    /// <summary>
    /// Convenience: find the Start node (every well-formed group has
    /// exactly one). Returns null if a caller hands us a malformed graph.
    /// </summary>
    public NodeViewModel? Start => Nodes.OfType<StartNodeViewModel>().FirstOrDefault();

    /// <summary>
    /// Reorder two nodes within the same parallel stage. Parallel
    /// stages have no semantic order (they all run concurrently) but
    /// visual order matters to the user. Implementation: reshuffle the
    /// edges feeding the parallel block from each predecessor so the
    /// dragged node's incoming edge sits immediately after the
    /// target node's incoming edge — the stage rebuilder uses edge
    /// iteration order to fix the per-stage display order.
    /// </summary>
    public void ReorderWithinStage(StageViewModel stage, NodeViewModel dragged, NodeViewModel target)
    {
        if (ReferenceEquals(dragged, target)) return;
        var stageIds = stage.Nodes.Select(n => n.Id).ToHashSet();
        if (!stageIds.Contains(dragged.Id) || !stageIds.Contains(target.Id)) return;

        var predIds = Edges
            .Where(e => stageIds.Contains(e.To))
            .Select(e => e.From)
            .Distinct()
            .ToList();

        foreach (var pred in predIds)
        {
            var draggedEdge = Edges.FirstOrDefault(e => e.From == pred && e.To == dragged.Id);
            var targetEdge = Edges.FirstOrDefault(e => e.From == pred && e.To == target.Id);
            if (draggedEdge is null || targetEdge is null) continue;

            Edges.Remove(draggedEdge);
            var targetIndex = Edges.IndexOf(targetEdge);
            // Insert immediately after the target edge so a left→right
            // pass over Edges meets target first, then dragged.
            Edges.Insert(targetIndex + 1, draggedEdge);
        }

        RebuildStages();
    }

    /// <summary>
    /// Merge <paramref name="node"/> into <paramref name="targetStage"/>
    /// as a parallel sibling of its existing nodes. The dragged node
    /// inherits all of the target stage's incoming predecessors and
    /// outgoing successors, so the stage rebuilder lays it out
    /// side-by-side under the same "Run in parallel" container.
    ///
    /// Works whether the target stage is already parallel (multiple
    /// nodes sharing predecessors+successors) or a single-node stage
    /// — merging into the latter promotes it into a parallel block.
    /// </summary>
    public void MergeIntoStage(NodeViewModel node, StageViewModel targetStage)
    {
        if (node is StartNodeViewModel) return;
        if (targetStage.Nodes.Any(n => ReferenceEquals(n, node))) return;
        if (targetStage.Nodes.OfType<StartNodeViewModel>().Any()) return;

        // -- Step 1: disconnect dragged from its current position.
        var ins = Edges.Where(e => e.To == node.Id).Select(e => e.From).Distinct().ToList();
        var outs = Edges.Where(e => e.From == node.Id).Select(e => e.To).Distinct().ToList();
        var dropEdges = Edges.Where(e => e.From == node.Id || e.To == node.Id).ToList();
        foreach (var e in dropEdges) Edges.Remove(e);

        // Linear extraction restitch (see MoveNodeAfterStage for the
        // why) — parallel siblings already maintain connectivity, so
        // skip in that case to avoid Start→Z shortcut bugs.
        var draggedStage = Stages.FirstOrDefault(s => s.Nodes.Any(n => n.Id == node.Id));
        var isLinearExtraction = draggedStage?.Nodes.Count == 1;
        if (isLinearExtraction)
        {
            foreach (var inId in ins)
            {
                foreach (var outId in outs)
                {
                    if (Edges.Any(x => x.From == inId && x.To == outId)) continue;
                    Edges.Add(new EdgeViewModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        From = inId,
                        To = outId,
                    });
                }
            }
        }

        // -- Step 2: wire dragged with the target stage's shared
        // predecessors and successors.
        var stageIds = targetStage.Nodes
            .Where(n => !ReferenceEquals(n, node))
            .Select(n => n.Id)
            .ToHashSet();

        var stagePreds = Edges
            .Where(e => stageIds.Contains(e.To))
            .Select(e => e.From)
            .Distinct()
            .ToList();
        var stageSuccs = Edges
            .Where(e => stageIds.Contains(e.From))
            .Select(e => e.To)
            .Distinct()
            .ToList();

        foreach (var pred in stagePreds)
        {
            if (Edges.Any(e => e.From == pred && e.To == node.Id)) continue;
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = pred,
                To = node.Id,
            });
        }
        foreach (var succ in stageSuccs)
        {
            if (Edges.Any(e => e.From == node.Id && e.To == succ)) continue;
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = node.Id,
                To = succ,
            });
        }

        RebuildStages();
    }

    /// <summary>
    /// Move <paramref name="node"/> so it runs *immediately before*
    /// <paramref name="targetStage"/>. Implemented as "move after the
    /// stage above <paramref name="targetStage"/>" — the stages are
    /// strictly ordered, and "before" only differs from "after" by
    /// targeting the predecessor. "Before Stage 0 (Start)" is impossible,
    /// so that degrades to "after Start" (first position) rather than
    /// silently refusing — a refusal after the caller has already
    /// detached the node would leave it orphaned.
    /// </summary>
    public void MoveNodeBeforeStage(NodeViewModel node, StageViewModel targetStage)
    {
        if (node is StartNodeViewModel) return;
        var idx = Stages.IndexOf(targetStage);
        if (idx < 0) return;
        if (idx == 0)
        {
            MoveNodeAfterStage(node, targetStage);
            return;
        }
        var predecessor = Stages[idx - 1];
        // If the user is dragging a node that's already in the predecessor
        // stage, "move before targetStage" would round-trip back to the
        // same position — refuse.
        if (predecessor.Nodes.Any(n => n.Id == node.Id)) return;
        MoveNodeAfterStage(node, predecessor);
    }

    /// <summary>
    /// Move <paramref name="node"/> (already in the graph) so it runs
    /// immediately after <paramref name="targetStage"/>. Used by the
    /// drag-reorder flow.
    ///
    /// Steps:
    /// 1. Disconnect the node from its current position, re-stitching
    ///    its previous predecessors directly to its previous successors
    ///    so the rest of the graph stays valid.
    /// 2. Splice it in after <paramref name="targetStage"/>, re-using
    ///    the same logic as <see cref="InsertAfterStage"/>.
    ///
    /// No-op if the node is the Start node (refuse to move it) or
    /// if it's already in the target stage (avoid a self-noop that
    /// would corrupt the topology).
    /// </summary>
    public void MoveNodeAfterStage(NodeViewModel node, StageViewModel targetStage)
    {
        if (node is StartNodeViewModel) return;

        // True no-op: dragged is the sole node in target → already there.
        var draggedStage = Stages.FirstOrDefault(s => s.Nodes.Any(n => n.Id == node.Id));
        if (draggedStage is not null
            && ReferenceEquals(draggedStage, targetStage)
            && draggedStage.Nodes.Count == 1)
        {
            return;
        }

        // -- Step 1: disconnect from current position, restitch.
        var ins = Edges.Where(e => e.To == node.Id).Select(e => e.From).Distinct().ToList();
        var outs = Edges.Where(e => e.From == node.Id).Select(e => e.To).Distinct().ToList();
        var dropEdges = Edges.Where(e => e.From == node.Id || e.To == node.Id).ToList();
        foreach (var e in dropEdges) Edges.Remove(e);

        // Restitch in×out direct edges *only* if the node sat alone in
        // its stage (the linear case). When it had parallel siblings,
        // those siblings already maintain the in→out connectivity, so
        // adding direct edges would let the predecessor short-circuit
        // the whole parallel block (Start→Z bypass bug).
        var isLinearExtraction = draggedStage?.Nodes.Count == 1;
        if (isLinearExtraction)
        {
            foreach (var inId in ins)
            {
                foreach (var outId in outs)
                {
                    if (Edges.Any(x => x.From == inId && x.To == outId)) continue;
                    Edges.Add(new EdgeViewModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        From = inId,
                        To = outId,
                    });
                }
            }
        }

        // -- Step 2: insert at the new position.
        // Exclude the dragged node from the target stage's IDs — it
        // appears in the StageViewModel's Nodes collection until
        // RebuildStages runs, and including it would emit a self-loop
        // edge (node → node) that breaks the topology.
        var stageIds = targetStage.Nodes
            .Where(n => !ReferenceEquals(n, node))
            .Select(n => n.Id)
            .ToHashSet();
        var outgoingFromStage = Edges.Where(e => stageIds.Contains(e.From)).ToList();
        var newOutgoingTargets = outgoingFromStage.Select(e => e.To).Distinct().ToList();
        foreach (var e in outgoingFromStage) Edges.Remove(e);

        foreach (var src in stageIds)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = src,
                To = node.Id,
            });
        }
        foreach (var target in newOutgoingTargets)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = node.Id,
                To = target,
            });
        }

        RebuildStages();
    }

    /// <summary>
    /// Insert <paramref name="newNode"/> immediately after the given
    /// stage, splitting every edge that leaves a node in this stage and
    /// goes into the next stage. Concretely: every edge `(s in stage) → t`
    /// gets rewired as `(s in stage) → newNode` plus a new
    /// `newNode → t`. If the stage was the last (no outgoing edges),
    /// the new node simply becomes the new tail.
    /// </summary>
    public void InsertAfterStage(StageViewModel stage, NodeViewModel newNode)
    {
        if (string.IsNullOrEmpty(newNode.Id)) newNode.Id = Guid.NewGuid().ToString();
        Nodes.Add(newNode);

        var stageIds = stage.Nodes.Select(n => n.Id).ToHashSet();
        var outgoingFromStage = Edges.Where(e => stageIds.Contains(e.From)).ToList();

        // Rewire outgoing-from-stage edges through newNode. Deduplicate
        // by target so a parallel-merge stage doesn't fan into newNode
        // multiple times.
        var newOutgoingTargets = outgoingFromStage.Select(e => e.To).Distinct().ToList();
        foreach (var e in outgoingFromStage) Edges.Remove(e);

        foreach (var src in stageIds)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = src,
                To = newNode.Id,
            });
        }

        foreach (var target in newOutgoingTargets)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = newNode.Id,
                To = target,
            });
        }

        RebuildStages();
    }

    /// <summary>
    /// Convert a parallel stage (N nodes sharing the same predecessors
    /// and same successors) into a sequential chain. Preserves the
    /// stage's current visual order — the first card becomes head of
    /// the chain, the last card becomes its tail.
    ///
    /// No-op if the stage has fewer than two nodes (nothing to
    /// sequence).
    /// </summary>
    public void MakeStageSequential(StageViewModel stage)
    {
        if (stage.Nodes.Count < 2) return;

        var nodesInOrder = stage.Nodes.ToList();
        var stageIds = nodesInOrder.Select(n => n.Id).ToHashSet();

        // Collect everything pointing into the parallel block, and
        // everything the parallel block points out to. After the
        // rewrite, only the first node receives the incomings and only
        // the last node emits the outgoings.
        var incoming = Edges.Where(e => stageIds.Contains(e.To)).ToList();
        var outgoing = Edges.Where(e => stageIds.Contains(e.From)).ToList();
        var incomingSources = incoming.Select(e => e.From).Distinct().ToList();
        var outgoingTargets = outgoing.Select(e => e.To).Distinct().ToList();

        foreach (var e in incoming) Edges.Remove(e);
        foreach (var e in outgoing) Edges.Remove(e);

        var first = nodesInOrder[0];
        var last = nodesInOrder[^1];

        foreach (var src in incomingSources)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = src,
                To = first.Id,
            });
        }

        // Chain: node[i] → node[i+1]
        for (var i = 0; i < nodesInOrder.Count - 1; i++)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = nodesInOrder[i].Id,
                To = nodesInOrder[i + 1].Id,
            });
        }

        foreach (var target in outgoingTargets)
        {
            Edges.Add(new EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = last.Id,
                To = target,
            });
        }

        RebuildStages();
    }

    /// <summary>
    /// Convert a sequence of single-node stages back into one parallel
    /// stage. Caller supplies the contiguous run of stages. Inverse of
    /// <see cref="MakeStageSequential"/>; primarily useful as an
    /// "undo" affordance for the same operation.
    /// </summary>
    public void MakeStagesParallel(IList<StageViewModel> stages)
    {
        if (stages.Count < 2) return;
        var nodes = stages.SelectMany(s => s.Nodes).ToList();
        if (nodes.Count < 2) return;

        var nodeIds = nodes.Select(n => n.Id).ToHashSet();

        // Everything that fed into the first stage and out of the last
        // stage becomes shared incoming / outgoing for every node.
        var firstStageIds = stages[0].Nodes.Select(n => n.Id).ToHashSet();
        var lastStageIds = stages[^1].Nodes.Select(n => n.Id).ToHashSet();
        var incomingSources = Edges
            .Where(e => firstStageIds.Contains(e.To) && !nodeIds.Contains(e.From))
            .Select(e => e.From).Distinct().ToList();
        var outgoingTargets = Edges
            .Where(e => lastStageIds.Contains(e.From) && !nodeIds.Contains(e.To))
            .Select(e => e.To).Distinct().ToList();

        // Drop every edge touching these nodes.
        foreach (var e in Edges.Where(e => nodeIds.Contains(e.From) || nodeIds.Contains(e.To)).ToList())
        {
            Edges.Remove(e);
        }

        // Fan-out from incoming sources to every node; fan-in from every
        // node to outgoing targets.
        foreach (var n in nodes)
        {
            foreach (var src in incomingSources)
            {
                Edges.Add(new EdgeViewModel
                {
                    Id = Guid.NewGuid().ToString(),
                    From = src, To = n.Id,
                });
            }
            foreach (var target in outgoingTargets)
            {
                Edges.Add(new EdgeViewModel
                {
                    Id = Guid.NewGuid().ToString(),
                    From = n.Id, To = target,
                });
            }
        }

        RebuildStages();
    }
}
