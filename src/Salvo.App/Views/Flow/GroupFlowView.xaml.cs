using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Salvo.App.Controls;
using Salvo.App.ViewModels;
using Salvo.App.ViewModels.Flow;

namespace Salvo.App.Views.Flow;

public partial class GroupFlowView : UserControl
{
    private const string NodeDragFormat = "Salvo.NodeRow";

    private Point _dragStart;
    private NodeViewModel? _dragSource;
    private FrameworkElement? _dragSourceElement;

    private DragGhostAdorner? _ghost;
    private AdornerLayer? _ghostLayer;
    private UIElement? _ghostHost;

    // Selected snap point during drag: index into a stable boundary
    // list built from rows under the cursor. -1 = no active target.
    private int _activeSnapIndex = -1;

    // When set, the cursor is hovering over a row's centre — drop here
    // merges the dragged node into that row's stage as a parallel
    // sibling instead of doing a sequential reorder.
    private NodeViewModel? _activeMergeRow;

    public GroupFlowView()
    {
        InitializeComponent();
    }

    // ---- "+ Insert below" wiring -------------------------------------

    private void InsertBelow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void InsertMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string kind) return;

        var menu = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu;
        var owner = menu?.PlacementTarget as FrameworkElement;
        if (owner?.Tag is not StageViewModel stage) return;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.InsertNodeAfterStage(stage, kind);
        }
    }

    // ---- Drag-and-drop reorder ---------------------------------------

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;

        var source = FindNodeRow(e.OriginalSource as DependencyObject);
        if (source is null) return;
        if (source.DataContext is not NodeViewModel node) return;
        if (node is StartNodeViewModel) return;

        _dragStart = e.GetPosition(this);
        _dragSource = node;
        _dragSourceElement = source;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_dragSource is null || _dragSourceElement is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ShowGhost(_dragSourceElement, e.GetPosition(_dragSourceElement));
        var prevOpacity = _dragSourceElement.Opacity;
        _dragSourceElement.Opacity = 0.35;

        var data = new DataObject(NodeDragFormat, _dragSource);
        try
        {
            DragDrop.DoDragDrop(_dragSourceElement, data, DragDropEffects.Move);
        }
        finally
        {
            HideGhost();
            HideInsertionLine();
            ArmMerge(null);
            _activeSnapIndex = -1;
            if (_dragSourceElement is not null)
            {
                _dragSourceElement.Opacity = prevOpacity;
            }
            _dragSource = null;
            _dragSourceElement = null;
        }
    }

    // ---- Per-stage drop handlers (kept so each stage panel is a valid
    //      drop target — the actual snap logic lives in OnDragOver below).

    /// <summary>
    /// The snap calculation is purely position-based (cursor's Y in
    /// <see cref="StageList"/> coords), so we don't need the sender to
    /// be a stage — any element with a valid DragEventArgs works. This
    /// lets the same logic serve both the per-stage handlers *and* the
    /// UserControl-level fallback for off-stage drops.
    /// </summary>
    private void Stage_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(NodeDragFormat))
        {
            e.Effects = DragDropEffects.None;
            HideInsertionLine();
            e.Handled = true;
            return;
        }

        if (_ghost is not null && _ghostHost is not null)
        {
            _ghost.UpdatePosition(e.GetPosition(_ghostHost));
        }

        var cursorInList = e.GetPosition(StageList);
        var boundaries = CollectRowBoundaries();
        if (boundaries.Count == 0)
        {
            HideInsertionLine();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Mode pick: cursor inside the middle 40% band of a row → merge
        // mode; otherwise snap-to-boundary. Hysteresis: once merge is
        // armed on a row, we keep it armed for the entire vertical span
        // of that row, so the small cursor wobble that happens during
        // the click-release doesn't kick us back into snap mode and
        // turn the merge drop into a sequential reorder.
        var rowUnderCursor = FindRowAtY(cursorInList.Y);
        if (rowUnderCursor is { Node: var hoverNode, TopY: var top, BottomY: var bot }
            && hoverNode is not StartNodeViewModel
            && !ReferenceEquals(hoverNode, _dragSource)
            && IsParallelizable(_dragSource)
            && IsParallelizable(hoverNode)
            && !AreInSameStage(_dragSource, hoverNode))
        {
            var height = bot - top;
            var dyFromTop = cursorInList.Y - top;
            var relative = height > 0 ? dyFromTop / height : 0.5;

            var alreadyArmedOnThisRow = ReferenceEquals(_activeMergeRow, hoverNode);
            var enteringMerge = relative > 0.30 && relative < 0.70;

            if (enteringMerge || alreadyArmedOnThisRow)
            {
                ArmMerge(hoverNode);
                HideInsertionLine();
                _activeSnapIndex = -1;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
        }

        // Sequential snap-to-boundary mode.
        ArmMerge(null);
        var snapIdx = FindClosestBoundary(boundaries, cursorInList.Y);
        var snap = boundaries[snapIdx];

        _activeSnapIndex = snapIdx;
        ShowInsertionLineAt(snap.YInList);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private (NodeViewModel Node, double TopY, double BottomY)? FindRowAtY(double yInList)
    {
        var rows = new List<(FrameworkElement, NodeViewModel, StageViewModel?, double, double)>();
        CollectRows(StageList, rows);
        foreach (var r in rows)
        {
            if (yInList >= r.Item4 && yInList <= r.Item5)
            {
                return (r.Item2, r.Item4, r.Item5);
            }
        }
        return null;
    }

    /// <summary>
    /// Only "executable" node types (apps, services, commands, group
    /// calls) make sense to run in parallel. Wait/IfElse are sequential
    /// control flow — putting them inside a parallel block would mean
    /// "stretch the block to at least N seconds" or "branch the parallel
    /// into two paths", both of which are confusing and have clearer
    /// expressions as sequential nodes after the block.
    /// </summary>
    private static bool IsParallelizable(NodeViewModel? node) => node is
        AppNodeViewModel or
        ServiceStartNodeViewModel or
        ServiceStopNodeViewModel or
        RunCommandNodeViewModel or
        GroupCallNodeViewModel;

    /// <summary>
    /// True when both nodes share the same flow-graph stage (they're
    /// already parallel siblings). Used to suppress the merge highlight
    /// inside the same parallel block — a "merge" there would be a
    /// no-op, and the snap-line reorder is the only meaningful drop.
    /// </summary>
    private bool AreInSameStage(NodeViewModel? a, NodeViewModel? b)
    {
        if (a is null || b is null) return false;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGroup is null) return false;
        return vm.SelectedGroup.Graph.Stages
            .Any(s => s.Nodes.Contains(a) && s.Nodes.Contains(b));
    }

    private void ArmMerge(NodeViewModel? node)
    {
        if (ReferenceEquals(_activeMergeRow, node)) return;
        if (_activeMergeRow is not null) _activeMergeRow.IsMergeTarget = false;
        _activeMergeRow = node;
        if (_activeMergeRow is not null) _activeMergeRow.IsMergeTarget = true;
    }

    private void Stage_OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// Fallback DragOver for areas of the view outside any stage's
    /// own DragOver region (the toolbar strip, empty space below the
    /// last stage). Same snap-line logic; just keeps the visual feedback
    /// continuous even when the cursor leaves the stage list.
    /// </summary>
    private void UserControl_OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        Stage_OnDragOver(this, e);
    }

    /// <summary>
    /// Fallback Drop for the same off-stage area. Without this, releases
    /// over the toolbar or the empty bottom of the scroll viewport would
    /// silently fail because no per-stage handler catches them.
    /// </summary>
    private void UserControl_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        Stage_OnDrop(this, e);
    }

    private void Stage_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(NodeDragFormat) is not NodeViewModel dragged) { ResetDrop(); return; }
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGroup is null) { ResetDrop(); return; }

        var graph = vm.SelectedGroup.Graph;
        var cursorInList = e.GetPosition(StageList);

        // Re-decide mode from the release position rather than trusting
        // whatever state the last DragOver left in _activeMergeRow /
        // _activeSnapIndex. Release-time jitter can otherwise flip the
        // mode between visual and apply, making the drop appear to
        // silently fail.
        var rowUnderCursor = FindRowAtY(cursorInList.Y);
        if (rowUnderCursor is { Node: var hoverNode, TopY: var top, BottomY: var bot }
            && hoverNode is not StartNodeViewModel
            && !ReferenceEquals(hoverNode, dragged)
            && IsParallelizable(dragged)
            && IsParallelizable(hoverNode)
            && !AreInSameStage(dragged, hoverNode))
        {
            var height = bot - top;
            var dyFromTop = cursorInList.Y - top;
            var relative = height > 0 ? dyFromTop / height : 0.5;
            // More forgiving merge zone on Drop than on DragOver: 80%
            // of the row counts as merge intent on release, so a small
            // cursor wobble in the click-release doesn't flip a merge
            // into a sequential snap.
            if (relative > 0.10 && relative < 0.90)
            {
                var targetStage = graph.Stages
                    .FirstOrDefault(s => s.Nodes.Any(n => ReferenceEquals(n, hoverNode)));
                if (targetStage is not null)
                {
                    graph.MergeIntoStage(dragged, targetStage);
                    vm.PersistConfigPublic();
                }
                ResetDrop();
                e.Handled = true;
                return;
            }
        }

        // Sequential snap-to-boundary fallback.
        var boundaries = CollectRowBoundaries();
        if (boundaries.Count == 0) { ResetDrop(); return; }
        var snapIdx = FindClosestBoundary(boundaries, cursorInList.Y);
        ApplyDrop(vm, dragged, boundaries, snapIdx);
        ResetDrop();
        e.Handled = true;
    }

    private void ResetDrop()
    {
        HideInsertionLine();
        _activeSnapIndex = -1;
        ArmMerge(null);
    }

    // ---- Insertion line + snap-point math ----------------------------

    /// <summary>
    /// One boundary in the rendered row list. The insertion line snaps
    /// to <see cref="YInList"/>; on drop the surrounding rows
    /// (<see cref="Above"/> / <see cref="Below"/>) tell us what graph
    /// operation to apply. Both can be null at the very top / very
    /// bottom of the list.
    /// </summary>
    private readonly record struct RowBoundary(
        double YInList,
        FrameworkElement? Above,
        NodeViewModel? AboveNode,
        StageViewModel? AboveStage,
        FrameworkElement? Below,
        NodeViewModel? BelowNode,
        StageViewModel? BelowStage);

    /// <summary>
    /// Walk the visual tree of the outer ItemsControl, collect every
    /// row's Y range in StageList-local coordinates, and emit the
    /// boundaries that sit between adjacent rows (plus one above the
    /// first row and one below the last).
    /// </summary>
    private List<RowBoundary> CollectRowBoundaries()
    {
        var rows = new List<(FrameworkElement Row, NodeViewModel Node, StageViewModel? Stage, double Top, double Bottom)>();
        CollectRows(StageList, rows);
        rows.Sort((a, b) => a.Top.CompareTo(b.Top));

        var result = new List<RowBoundary>();
        if (rows.Count == 0) return result;

        // Above the first row.
        result.Add(new RowBoundary(rows[0].Top, null, null, null, rows[0].Row, rows[0].Node, rows[0].Stage));

        // Between each adjacent pair.
        for (var i = 0; i < rows.Count - 1; i++)
        {
            var top = rows[i];
            var bot = rows[i + 1];
            // Snap line at the midpoint between rows for symmetric feel.
            var y = (top.Bottom + bot.Top) / 2;
            result.Add(new RowBoundary(y, top.Row, top.Node, top.Stage, bot.Row, bot.Node, bot.Stage));
        }

        // Below the last row.
        var last = rows[^1];
        result.Add(new RowBoundary(last.Bottom, last.Row, last.Node, last.Stage, null, null, null));

        return result;
    }

    private void CollectRows(DependencyObject root, List<(FrameworkElement, NodeViewModel, StageViewModel?, double, double)> sink)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.DataContext is NodeViewModel node && fe.ActualHeight > 0)
            {
                // Only count the outermost element bound to this node —
                // the row's template root, not its inner sub-elements
                // (which also inherit the node DataContext).
                if (sink.Any(r => ReferenceEquals(r.Item2, node))) continue;
                var topLeft = fe.TranslatePoint(new Point(0, 0), StageList);
                var stage = FindAncestorStage(fe);
                sink.Add((fe, node, stage, topLeft.Y, topLeft.Y + fe.ActualHeight));
            }
            CollectRows(child, sink);
        }
    }

    private static StageViewModel? FindAncestorStage(DependencyObject source)
    {
        var cur = source;
        while (cur is not null)
        {
            if (cur is FrameworkElement fe && fe.DataContext is StageViewModel stage)
            {
                return stage;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private static int FindClosestBoundary(List<RowBoundary> boundaries, double y)
    {
        var bestIdx = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < boundaries.Count; i++)
        {
            var d = Math.Abs(boundaries[i].YInList - y);
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }
        return bestIdx;
    }

    /// <summary>
    /// Translate the snapped boundary into the right graph operation.
    /// Three cases:
    /// 1. Above + below in the same stage as dragged → parallel reorder
    ///    (insert dragged between them within the parallel).
    /// 2. Boundary at top of a stage (above=null or above in a
    ///    different stage) → MoveBeforeStage of the below stage.
    /// 3. Boundary at bottom of a stage (below=null or below in a
    ///    different stage) → MoveAfterStage of the above stage.
    /// </summary>
    private static void ApplyDrop(MainWindowViewModel vm, NodeViewModel dragged, List<RowBoundary> boundaries, int idx)
    {
        if (vm.SelectedGroup is null) return;
        var graph = vm.SelectedGroup.Graph;
        var b = boundaries[idx];

        var draggedStage = boundaries.FirstOrDefault(r =>
                ReferenceEquals(r.AboveNode, dragged) || ReferenceEquals(r.BelowNode, dragged))
            is { } found
            ? (ReferenceEquals(found.AboveNode, dragged) ? found.AboveStage : found.BelowStage)
            : null;

        // Same-stage reorder (parallel): both sides of the boundary are
        // in the dragged node's current stage.
        if (draggedStage is not null
            && b.AboveStage is not null && ReferenceEquals(b.AboveStage, draggedStage)
            && b.BelowStage is not null && ReferenceEquals(b.BelowStage, draggedStage)
            && b.AboveNode is not null && b.BelowNode is not null)
        {
            // Place dragged immediately after AboveNode within the
            // parallel — which is equivalent to "immediately before
            // BelowNode" inside that parallel.
            graph.ReorderWithinStage(draggedStage, dragged, b.AboveNode);
            vm.PersistConfigPublic();
            return;
        }

        // Cross-stage move (or move out of parallel into sequence).
        if (b.AboveStage is not null)
        {
            graph.MoveNodeAfterStage(dragged, b.AboveStage);
        }
        else if (b.BelowStage is not null)
        {
            graph.MoveNodeBeforeStage(dragged, b.BelowStage);
        }
        vm.PersistConfigPublic();
    }

    private void ShowInsertionLineAt(double yInList)
    {
        // Translate from StageList coordinates to the overlay Grid
        // (StageList's parent) so the line tracks scroll.
        var inOverlay = StageList.TranslatePoint(new Point(0, yInList), (UIElement)StageList.Parent);
        InsertionLineTransform.Y = inOverlay.Y - InsertionLine.Height / 2;
        InsertionLine.Visibility = Visibility.Visible;
    }

    private void HideInsertionLine()
    {
        InsertionLine.Visibility = Visibility.Collapsed;
    }

    // ---- Hit-testing helpers -----------------------------------------

    private static FrameworkElement? FindNodeRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement fe && fe.DataContext is NodeViewModel)
            {
                return fe;
            }
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or ComboBox or RangeBase)
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---- Drag ghost --------------------------------------------------

    private void ShowGhost(FrameworkElement source, Point grabPoint)
    {
        HideGhost();
        _ghostLayer = AdornerLayer.GetAdornerLayer(source);
        _ghostHost = source;
        if (_ghostLayer is null) return;
        _ghost = new DragGhostAdorner(source, source, grabPoint);
        _ghostLayer.Add(_ghost);
    }

    private void HideGhost()
    {
        if (_ghost is not null && _ghostLayer is not null)
        {
            _ghostLayer.Remove(_ghost);
        }
        _ghost = null;
        _ghostLayer = null;
        _ghostHost = null;
    }
}
