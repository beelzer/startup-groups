using System.Runtime.InteropServices;
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

    // Currently highlighted If branch during a drag. Tracked like
    // _activeMergeRow (set on branch hover, cleared when the drag moves
    // over the outer list or ends) rather than via DragLeave — DragLeave
    // fires on every child-element crossing and flickers the highlight.
    private IfElseNodeViewModel? _activeBranchNode;
    private string? _activeBranchName;

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

    // Trailing always-visible "+ Add item" — pops the kind picker. Reuses
    // MainWindowViewModel.AddNode (RelayCommand) which appends after the
    // graph's current leaves; that's the right "add to end" semantic.

    private void TrailingAdd_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void TrailingAddMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string kind) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.AddNodeCommand.CanExecute(kind))
        {
            vm.AddNodeCommand.Execute(kind);
        }
    }

    // ---- If-branch add wiring ----------------------------------------

    private void BranchAdd_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void BranchAddMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string kind) return;

        // The placement target is the "+ Add to Then/Else" button. Its Tag
        // names the branch; its DataContext is the owning If node.
        var menu = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu;
        var owner = menu?.PlacementTarget as FrameworkElement;
        if (owner?.Tag is not string branch) return;
        if (owner.DataContext is not IfElseNodeViewModel ifNode) return;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.AddNodeToBranch(ifNode, branch, kind);
        }
    }

    /// <summary>
    /// True only for nodes that live directly in the outer graph. Branch
    /// nodes (held on an If view-model) render inside the If card but are
    /// *not* outer nodes, so the drag-reorder system must ignore them —
    /// dragging one would rewire the outer topology it isn't part of.
    /// </summary>
    private bool IsOuterNode(NodeViewModel node)
        => DataContext is MainWindowViewModel vm
           && vm.SelectedGroup is not null
           && vm.SelectedGroup.Graph.Nodes.Contains(node);

    // ---- In-card field persistence ------------------------------------

    // Every editable field inside a node card (condition kind + value,
    // wait duration, service names, run-command interpreter + text, group
    // picker) updates only the VM; nothing else persists it — structural
    // edits save, field edits didn't. Flush the config on LostFocus /
    // DropDownClosed so the edit survives without waiting for the next
    // structural change. DropDownClosed (not SelectionChanged) avoids a
    // save on every virtualization realize.
    private void NodeField_OnChanged(object sender, RoutedEventArgs e) => PersistFieldEdit();

    private void NodePicker_OnClosed(object? sender, EventArgs e) => PersistFieldEdit();

    private void PersistFieldEdit()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PersistConfigPublic();
        }
    }

    // ---- Drag a node into an If then/else branch ---------------------

    private void Branch_OnDragOver(object sender, DragEventArgs e)
    {
        // Keep the floating drag-name ghost tracking the cursor over
        // branches too — otherwise it freezes at the box edge.
        if (_ghost is not null && _ghostHost is not null)
        {
            _ghost.UpdatePosition(e.GetPosition(_ghostHost));
        }

        if (sender is not FrameworkElement fe
            || fe.DataContext is not IfElseNodeViewModel ifNode
            || fe.Tag is not string branch
            || e.Data.GetData(NodeDragFormat) is not NodeViewModel dragged
            || !CanDropIntoBranch(dragged, ifNode))
        {
            ArmBranch(null, null);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ArmBranch(ifNode, branch);
        HideInsertionLine();   // suppress the outer snap-line while over a branch
        ArmMerge(null);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    // DragLeave is intentionally a near no-op — the highlight is cleared by
    // ArmBranch when the drag moves over the outer list or ends, not on
    // every child-crossing leave (which flickered). We just swallow the
    // event so it doesn't bubble to the outer handlers.
    private void Branch_OnDragLeave(object sender, DragEventArgs e) => e.Handled = true;

    private void Branch_OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe
            || fe.DataContext is not IfElseNodeViewModel ifNode
            || fe.Tag is not string branch)
        {
            return;
        }

        if (e.Data.GetData(NodeDragFormat) is not NodeViewModel dragged || !CanDropIntoBranch(dragged, ifNode))
        {
            ResetDrop();
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.MoveNodeIntoBranch(dragged, ifNode, branch);
        }
        ResetDrop();
        e.Handled = true;
    }

    /// <summary>
    /// Set the single highlighted branch, clearing any previously
    /// highlighted one. Pass (null, null) to clear. Mirrors
    /// <see cref="ArmMerge"/> so branch highlighting is edge-triggered and
    /// flicker-free.
    /// </summary>
    private void ArmBranch(IfElseNodeViewModel? ifNode, string? branch)
    {
        if (ReferenceEquals(_activeBranchNode, ifNode) && _activeBranchName == branch) return;
        if (_activeBranchNode is not null && _activeBranchName is not null)
        {
            SetBranchDropTarget(_activeBranchNode, _activeBranchName, false);
        }
        _activeBranchNode = ifNode;
        _activeBranchName = branch;
        if (_activeBranchNode is not null && branch is not null)
        {
            SetBranchDropTarget(_activeBranchNode, branch, true);
        }
    }

    private static void SetBranchDropTarget(IfElseNodeViewModel ifNode, string branch, bool on)
    {
        if (string.Equals(branch, "else", StringComparison.Ordinal)) ifNode.ElseIsDropTarget = on;
        else ifNode.ThenIsDropTarget = on;
    }

    // A node may drop into a branch when it isn't the If itself, isn't
    // Start, and isn't another If (nested branches aren't supported by the
    // linear branch transform). Source may be an outer node or a node from
    // another branch.
    private static bool CanDropIntoBranch(NodeViewModel dragged, IfElseNodeViewModel ifNode)
        => !ReferenceEquals(dragged, ifNode)
           && dragged is not StartNodeViewModel
           && dragged is not IfElseNodeViewModel;

    // ---- Drag-and-drop reorder ---------------------------------------

    /// <summary>
    /// Tunnels before any child handles the drag, keeping the ghost on the
    /// cursor over most of the surface.
    /// </summary>
    private void Root_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (_ghost is not null && _ghostHost is not null)
        {
            _ghost.UpdatePosition(e.GetPosition(_ghostHost));
        }
    }

    /// <summary>
    /// Source-side drag feedback fires continuously for the whole drag no
    /// matter what the cursor is over — including inline text boxes /
    /// combo boxes that register as their own OS drop targets and swallow
    /// every DragOver. We use it to keep the ghost glued to the cursor
    /// (via the live cursor position, since drag events don't reach us
    /// over those controls).
    /// </summary>
    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        if (_ghost is null || _ghostHost is null) return;
        if (!GetCursorPos(out var pt)) return;
        try
        {
            // GetCursorPos + PointFromScreen both work in device pixels, so
            // this matches the DIU that DragOver's GetPosition yields — no
            // jump when handoff switches between the two.
            _ghost.UpdatePosition(_ghostHost.PointFromScreen(new Point(pt.X, pt.Y)));
        }
        catch (InvalidOperationException)
        {
            // Host not connected to a PresentationSource mid-drag; ignore.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        // Drop whatever a previous press armed — a click that never
        // crossed the drag threshold otherwise leaves _dragSource set, and
        // a later press on a TextBox / empty space would start dragging
        // the *old* node.
        _dragSource = null;
        _dragSourceElement = null;
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;

        var source = FindNodeRow(e.OriginalSource as DependencyObject);
        if (source is null) return;
        if (source.DataContext is not NodeViewModel node) return;
        if (node is StartNodeViewModel) return;
        // Both outer nodes and branch nodes are draggable: outer nodes
        // reorder / move into branches; branch nodes move out to the flow
        // list or across to the other branch. Only Start is pinned.

        _dragStart = e.GetPosition(this);
        _dragSource = node;
        _dragSourceElement = source;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        _dragSource = null;
        _dragSourceElement = null;
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
            ArmBranch(null, null);
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

        // Over the outer list, not a branch — drop any branch highlight.
        ArmBranch(null, null);

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

            // Middle 40% of a row's height arms a parallel-merge; the outer
            // bands fall through to sequential snap-to-boundary below.
            const double mergeBandLower = 0.30;
            const double mergeBandUpper = 0.70;

            var alreadyArmedOnThisRow = ReferenceEquals(_activeMergeRow, hoverNode);
            var enteringMerge = relative > mergeBandLower && relative < mergeBandUpper;

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

        // Decide the drop operation from the release position BEFORE any
        // graph mutation — both because release-time jitter can flip the
        // mode the last DragOver armed, and because a branch node must
        // only be lifted out of its branch (EnsureOuter below) once a
        // valid target is known. Extract-first left the node edgeless and
        // persisted disconnected whenever the decision fell through.
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
                if (targetStage is not null && EnsureOuter(vm, graph, dragged))
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
        if (EnsureOuter(vm, graph, dragged))
        {
            ApplyDrop(vm, dragged, boundaries, snapIdx);
        }
        ResetDrop();
        e.Handled = true;
    }

    /// <summary>
    /// A branch node dropped on the outer flow list must first be lifted
    /// out of its branch into the outer graph; outer nodes pass through.
    /// Returns false when the dragged node is in neither place (stale
    /// drag data) — the drop is then abandoned without mutating anything.
    /// </summary>
    private static bool EnsureOuter(MainWindowViewModel vm, GroupGraphViewModel graph, NodeViewModel dragged)
        => graph.Nodes.Contains(dragged) || vm.ExtractBranchNodeToOuter(dragged);

    private void ResetDrop()
    {
        HideInsertionLine();
        _activeSnapIndex = -1;
        ArmMerge(null);
        ArmBranch(null, null);
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
    /// boundaries that sit between adjacent rows (plus one below the
    /// last). There is deliberately no boundary above the first row —
    /// the first row is always Start and nothing can be placed before
    /// it, so that line only ever advertised a drop that no-oped.
    /// </summary>
    private List<RowBoundary> CollectRowBoundaries()
    {
        var rows = new List<(FrameworkElement Row, NodeViewModel Node, StageViewModel? Stage, double Top, double Bottom)>();
        CollectRows(StageList, rows);
        rows.Sort((a, b) => a.Top.CompareTo(b.Top));

        var result = new List<RowBoundary>();
        if (rows.Count == 0) return result;

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
            // Only count the outermost element bound to an *outer* node —
            // the row's template root, not its inner sub-elements (which
            // inherit the node DataContext), and not branch nodes nested
            // inside an If card (excluded via IsOuterNode).
            if (child is FrameworkElement fe && fe.DataContext is NodeViewModel node && fe.ActualHeight > 0
                && IsOuterNode(node)
                && !sink.Any(r => ReferenceEquals(r.Item2, node)))
            {
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
