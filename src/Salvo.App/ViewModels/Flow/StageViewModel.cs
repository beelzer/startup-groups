using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Salvo.App.ViewModels.Flow;

/// <summary>
/// One horizontal row of node cards in the Simple view — the set of
/// nodes that run concurrently because all their predecessors are
/// elsewhere in earlier stages. Computed by
/// <see cref="GroupGraphViewModel.RebuildStages"/> via topological BFS.
/// </summary>
public sealed partial class StageViewModel : ObservableObject
{
    [ObservableProperty] private int _index;

    /// <summary>
    /// True if this stage holds more than one node (i.e. they run in
    /// parallel). Drives the "Runs in parallel" subtitle in the view.
    /// </summary>
    public bool IsParallel => Nodes.Count > 1;

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// During a drag-reorder, the view-side drop logic sets one of these
    /// to true on the hovered stage to show an insertion bar at the top
    /// or bottom edge. They're cleared on drag-leave and drop. Mutually
    /// exclusive.
    /// </summary>
    [ObservableProperty] private bool _dropIndicatorTop;
    [ObservableProperty] private bool _dropIndicatorBottom;

    public StageViewModel()
    {
        Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsParallel));
    }
}
