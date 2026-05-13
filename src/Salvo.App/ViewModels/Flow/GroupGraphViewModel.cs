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
        vm.RebuildStages();
        return vm;
    }

    public void WriteTo(Group group)
    {
        group.Nodes.Clear();
        group.Edges.Clear();
        foreach (var n in Nodes) group.Nodes.Add(n.ToModel());
        foreach (var e in Edges) group.Edges.Add(e.ToModel());
    }

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
}
