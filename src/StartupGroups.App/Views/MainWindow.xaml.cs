using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Documents;
using StartupGroups.App.Animations;
using StartupGroups.App.Controls;
using StartupGroups.App.ViewModels;
using StartupGroups.Core.Branding;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Views;

public partial class MainWindow : FluentWindow
{
    private const string AppRowDragFormat = "StartupGroups.AppRow";
    private const string GroupRowDragFormat = "StartupGroups.GroupRow";
    private readonly MainWindowViewModel _viewModel;
    private bool _indicatorSettled;
    private Point _dragStart;
    private AppEntryViewModel? _dragSource;
    private Border? _dragSourceRow;
    private Border? _activeDropRow;
    private int? _activeAppInsertAt;
    private GroupViewModel? _groupDragSource;
    private System.Windows.Controls.ListViewItem? _groupDragSourceItem;
    private Point _groupDragStart;
    private System.Windows.Controls.ListViewItem? _activeGroupDropItem;
    private int? _activeGroupInsertAt;
    private DragGhostAdorner? _dragGhost;
    private AdornerLayer? _dragGhostLayer;
    private UIElement? _dragGhostHost;

    // Live reorder preview: every row gets a TranslateTransform, animated as the cursor moves so
    // the gap where the source would land opens up between its neighbours.
    private readonly List<(FrameworkElement Row, TranslateTransform Transform)> _previewRows = new();
    private FrameworkElement? _previewSourceRow;
    private int _previewSourceIndex = -1;
    private double _previewRowHeight;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        TryLoadIcon();

        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.AdminCardHighlightRequested += OnAdminCardHighlightRequested;
        GroupsList.LayoutUpdated += (_, _) => ScheduleIndicatorUpdate();

        Loaded += OnLoaded;
        Closed += OnClosedUnwatch;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Start();
        ScheduleIndicatorUpdate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedGroup) or nameof(MainWindowViewModel.ActiveView))
        {
            ScheduleIndicatorUpdate();
        }
    }

    private void ScheduleIndicatorUpdate()
    {
        Dispatcher.BeginInvoke(UpdateIndicator, DispatcherPriority.Loaded);
    }

    private void UpdateIndicator()
    {
        FrameworkElement? target = _viewModel.ActiveView switch
        {
            ActiveView.Groups when _viewModel.SelectedGroup is not null =>
                GroupsList.ItemContainerGenerator.ContainerFromItem(_viewModel.SelectedGroup) as FrameworkElement,
            ActiveView.WindowsStartup => WindowsStartupButton,
            ActiveView.Benchmarks => BenchmarksButton,
            ActiveView.Settings => SettingsButton,
            _ => null
        };

        if (target is null || target.ActualHeight <= 0)
        {
            AnimateOpacity(0, Durations.OpacityFadeFast);
            return;
        }

        GeneralTransform transform;
        try
        {
            transform = target.TransformToAncestor(SidebarRoot);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var itemTopLeft = transform.Transform(new Point(0, 0));
        var targetY = itemTopLeft.Y + (target.ActualHeight - SidebarIndicator.Height) / 2;

        if (!_indicatorSettled)
        {
            SidebarIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
            SidebarIndicatorTransform.Y = targetY;
            SidebarIndicator.Opacity = 1;
            _indicatorSettled = true;
            return;
        }

        AnimateY(targetY);
        AnimateOpacity(1, Durations.OpacityFadeFast);
    }

    private void AnimateY(double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = Durations.SidebarIndicatorSlide,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        SidebarIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateOpacity(double to, TimeSpan duration)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = duration
        };
        SidebarIndicator.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        ApplicationThemeManager.Apply(this);
    }

    private void OnClosedUnwatch(object? sender, EventArgs e)
    {
        _viewModel.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.AdminCardHighlightRequested -= OnAdminCardHighlightRequested;
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        try
        {
            SystemThemeWatcher.UnWatch(this);
        }
        catch (InvalidOperationException)
        {
            // Window HWND already destroyed; theme hooks are implicitly gone.
        }
    }


    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // Mouse XButton1/XButton2 (back/forward thumb buttons) drive the view history.
        if (e.ChangedButton == MouseButton.XButton1 && _viewModel.GoBackCommand.CanExecute(null))
        {
            _viewModel.GoBackCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.XButton2 && _viewModel.GoForwardCommand.CanExecute(null))
        {
            _viewModel.GoForwardCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    private void OnAdminCardHighlightRequested(object? sender, EventArgs e)
    {
        // Defer until the Settings scroll viewer has rendered, otherwise scroll math
        // runs before the admin card has a position.
        Dispatcher.BeginInvoke(() =>
        {
            CenterInScrollViewer(AdminModeCard);

            var effect = new DropShadowEffect
            {
                Color = Colors.Orange,
                BlurRadius = UiMetrics.AdminCardHighlightBlurRadius,
                ShadowDepth = 0,
                Opacity = 0
            };
            AdminModeCard.Effect = effect;

            var pulse = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = Durations.AdminCardPulse,
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(UiMetrics.AdminCardPulseRepeats),
                FillBehavior = FillBehavior.Stop
            };
            pulse.Completed += (_, _) => AdminModeCard.Effect = null;
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        }, DispatcherPriority.Background);
    }

    private static void CenterInScrollViewer(FrameworkElement target)
    {
        var scrollViewer = FindAncestor<ScrollViewer>(target);
        if (scrollViewer is null)
        {
            target.BringIntoView();
            return;
        }

        var transform = target.TransformToAncestor(scrollViewer);
        var topInViewer = transform.Transform(new Point(0, 0)).Y + scrollViewer.VerticalOffset;
        var centeredOffset = topInViewer - (scrollViewer.ViewportHeight - target.ActualHeight) / 2;

        var clamped = Math.Max(0, Math.Min(centeredOffset, scrollViewer.ScrollableHeight));
        scrollViewer.ScrollToVerticalOffset(clamped);
    }

    private void TryLoadIcon()
    {
        try
        {
            // Load the embedded PNG, not the .ico. WPF's IconBitmapDecoder doesn't
            // decode PNG-encoded frames inside .ico and silently falls back to the
            // small 16x16 BMP frame, which then displays as a tiny taskbar icon.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri("pack://application:,,,/Assets/app.png", UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            Icon = bitmap;
            TitleBarIcon.Source = bitmap;
        }
        catch
        {
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.MinimizeToTrayOnClose && Application.Current.Windows.Count > 0)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border row || row.Tag is not AppEntryViewModel app) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, row)) return;

        _dragStart = e.GetPosition(this);
        _dragSource = app;
        _dragSourceRow = row;
    }

    private static bool IsInsideButton(DependencyObject? element, DependencyObject row)
    {
        while (element is not null && !ReferenceEquals(element, row))
        {
            if (element is ButtonBase) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Row_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null || _dragSourceRow is null)
            return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(AppRowDragFormat, _dragSource);
        var grabPoint = e.GetPosition(_dragSourceRow);
        ShowDragGhost(_dragSourceRow, grabPoint);

        var itemsControl = FindAncestor<ItemsControl>(_dragSourceRow);
        var apps = _viewModel.SelectedGroup?.Apps;
        if (itemsControl is not null && apps is not null)
        {
            BeginReorderPreview(
                apps.Count,
                apps.IndexOf(_dragSource),
                i => itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is { } container
                    ? FindVisualChild<Border>(container, "RowRoot")
                    : null);
        }

        try
        {
            DragDrop.DoDragDrop(_dragSourceRow, data, DragDropEffects.Move);
        }
        finally
        {
            HideDragGhost();
            EndReorderPreview();
            ClearAppDropIndicator();
            _activeAppInsertAt = null;
            _dragSource = null;
            _dragSourceRow = null;
        }
    }

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border anchor || !e.Data.GetDataPresent(AppRowDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        UpdateDragGhost(e.GetPosition(this));

        var (target, isBelow) = ResolveAppDropTarget(anchor, e.GetPosition(anchor));
        if (target is null)
        {
            ClearAppDropIndicator();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Slide siblings out of the way so the gap shows where the ghost would land; the static
        // drop bar is redundant while the preview is live.
        if (target.Tag is AppEntryViewModel targetApp && _viewModel.SelectedGroup?.Apps is { } apps)
        {
            var targetIdx = apps.IndexOf(targetApp);
            if (targetIdx >= 0)
            {
                var insertAt = isBelow ? targetIdx + 1 : targetIdx;
                _activeAppInsertAt = insertAt;
                UpdateReorderPreview(insertAt);
            }
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Row_DragLeave(object sender, DragEventArgs e)
    {
        // Intentional no-op: clearing on DragLeave causes flicker between adjacent rows'
        // DragOver events. Indicators are cleared when another row takes over (see DragOver)
        // and when the drag ends (finally-block in PreviewMouseMove).
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(AppRowDragFormat) is not AppEntryViewModel source ||
            _viewModel.SelectedGroup is null)
        {
            return;
        }

        ClearAppDropIndicator();
        CommitAppReorder(source);
        e.Handled = true;
    }

    // Fallback for drops that land in an empty gap created by the reorder preview (e.g. the top
    // slot when rows above the source have slid down). The per-Border Row_Drop doesn't fire there
    // because no row's hit region covers the cursor.
    private void AppsContainer_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(AppRowDragFormat) is not AppEntryViewModel source) return;
        if (_viewModel.SelectedGroup is null) return;

        ClearAppDropIndicator();
        CommitAppReorder(source);
        e.Handled = true;
    }

    private void AppsContainer_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.Data.GetDataPresent(AppRowDragFormat)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void CommitAppReorder(AppEntryViewModel source)
    {
        if (_viewModel.SelectedGroup?.Apps is not { } apps) return;

        // Prefer the insertion index computed during DragOver — it's correct even when the preview
        // has translated rows out from under the cursor at drop time.
        if (_activeAppInsertAt is not int insertAt) return;

        var sourceIdx = apps.IndexOf(source);
        if (sourceIdx >= 0 && sourceIdx < insertAt) insertAt--;

        _viewModel.ReorderApp(source, insertAt);
    }

    // Walks the ItemsControl hosting `anchor` and picks a single drop target per gap: the first
    // row whose midpoint is below the cursor gets a top-edge indicator; if the cursor is past
    // every midpoint, the last row gets a bottom-edge indicator (insert-at-end).
    private (Border? Row, bool IsBelow) ResolveAppDropTarget(Border anchor, Point cursorInAnchor)
    {
        if (_viewModel.SelectedGroup?.Apps is not { Count: > 0 } apps) return (null, false);

        var itemsControl = FindAncestor<ItemsControl>(anchor);
        if (itemsControl is null) return (null, false);

        var cursorInList = anchor.TranslatePoint(cursorInAnchor, itemsControl);
        Border? last = null;

        for (var i = 0; i < apps.Count; i++)
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i);
            if (container is null) continue;
            var rowBorder = FindVisualChild<Border>(container, "RowRoot");
            if (rowBorder is null) continue;
            last = rowBorder;

            var topLeft = rowBorder.TranslatePoint(new Point(0, 0), itemsControl);
            var mid = topLeft.Y + rowBorder.ActualHeight / 2;
            if (cursorInList.Y < mid)
            {
                return (rowBorder, false);
            }
        }

        return (last, true);
    }

    private void ClearAppDropIndicator()
    {
        if (_activeDropRow is null) return;
        SetDropVisible(_activeDropRow, above: true, visible: false);
        SetDropVisible(_activeDropRow, above: false, visible: false);
        _activeDropRow = null;
    }

    // Attaches a translucent ghost of `source` to the window's AdornerLayer and seeds its initial
    // position. Callers invoke UpdateDragGhost from DragOver and HideDragGhost from the
    // DoDragDrop finally block.
    private void ShowDragGhost(FrameworkElement source, Point initialCursorInSource)
    {
        HideDragGhost();

        // Adorn the decorator inside MainWindow (FluentWindow's chrome template drops the default
        // AdornerDecorator, so GetAdornerLayer(this) returns null).
        var host = RootAdornerHost.Child as UIElement ?? RootAdornerHost;
        _dragGhostLayer = AdornerLayer.GetAdornerLayer(host);
        if (_dragGhostLayer is null) return;

        _dragGhost = new DragGhostAdorner(
            adornedElement: host,
            source: source,
            grabPoint: initialCursorInSource);

        _dragGhostLayer.Add(_dragGhost);

        var cursorInHost = source.TranslatePoint(initialCursorInSource, host);
        _dragGhost.UpdatePosition(cursorInHost);
        _dragGhostHost = host;
    }

    private void UpdateDragGhost(Point cursorInWindow)
    {
        if (_dragGhost is null || _dragGhostHost is null) return;
        var cursorInHost = TranslatePoint(cursorInWindow, _dragGhostHost);
        _dragGhost.UpdatePosition(cursorInHost);
    }

    private void HideDragGhost()
    {
        if (_dragGhost is not null && _dragGhostLayer is not null)
        {
            _dragGhostLayer.Remove(_dragGhost);
        }
        _dragGhost = null;
        _dragGhostLayer = null;
        _dragGhostHost = null;
    }

    private static TranslateTransform EnsurePreviewTransform(FrameworkElement row)
    {
        if (row.RenderTransform is TranslateTransform existing) return existing;
        var t = new TranslateTransform();
        row.RenderTransform = t;
        return t;
    }

    // Seeds the per-row TranslateTransforms and hides the source row in place. Caller is
    // responsible for passing the row-lookup delegate since ItemsControl vs ListView expose
    // different container types.
    private void BeginReorderPreview(int itemCount, int sourceIndex, Func<int, FrameworkElement?> rowAtIndex)
    {
        EndReorderPreview();
        if (sourceIndex < 0 || sourceIndex >= itemCount) return;

        _previewSourceIndex = sourceIndex;
        for (var i = 0; i < itemCount; i++)
        {
            var row = rowAtIndex(i);
            if (row is null) continue;
            _previewRows.Add((row, EnsurePreviewTransform(row)));
            if (i == sourceIndex)
            {
                _previewSourceRow = row;
                _previewRowHeight = row.ActualHeight;
            }
        }

        if (_previewSourceRow is not null)
        {
            _previewSourceRow.Opacity = 0.0;
        }
    }

    // Animates each row to its target offset given a proposed insertion index. Rows between the
    // source's old slot and the insertion point shift by ±rowHeight to open up the landing gap.
    private void UpdateReorderPreview(int insertAt)
    {
        if (_previewSourceRow is null || _previewRows.Count == 0) return;
        var s = _previewSourceIndex;
        var t = insertAt;
        var h = _previewRowHeight;

        for (var i = 0; i < _previewRows.Count; i++)
        {
            if (i == s) continue;
            var tr = _previewRows[i].Transform;

            double target;
            if (i > s && i < t) target = -h;
            else if (i < s && i >= t) target = h;
            else target = 0;

            if (Math.Abs(tr.Y - target) < UiMetrics.RowReorderSnapToleranceY) continue;
            var anim = new DoubleAnimation(target, Durations.RowReorderPreview)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            tr.BeginAnimation(TranslateTransform.YProperty, anim);
        }
    }

    private void EndReorderPreview()
    {
        foreach (var (_, tr) in _previewRows)
        {
            // Stop the animation and snap to 0 so the subsequent collection mutation lays rows out normally.
            tr.BeginAnimation(TranslateTransform.YProperty, null);
            tr.Y = 0;
        }
        _previewRows.Clear();

        if (_previewSourceRow is not null)
        {
            _previewSourceRow.Opacity = 1.0;
        }
        _previewSourceRow = null;
        _previewSourceIndex = -1;
        _previewRowHeight = 0;
    }

    private static void SetDropVisible(Border row, bool above, bool visible)
    {
        var name = above ? "DropAbove" : "DropBelow";
        if (row.FindName(name) is System.Windows.Shapes.Rectangle r)
        {
            r.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (row.Child is Grid wrapper)
        {
            foreach (var child in wrapper.Children)
            {
                if (child is System.Windows.Shapes.Rectangle rect && rect.Name == name)
                {
                    rect.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    return;
                }
            }
        }
    }

    private void GroupsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is GroupViewModel group)
        {
            _groupDragSource = group;
            _groupDragSourceItem = item;
            _groupDragStart = e.GetPosition(this);
        }
    }

    private void GroupsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _groupDragSource is null) return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _groupDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _groupDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(GroupRowDragFormat, _groupDragSource);
        var sourceItem = _groupDragSourceItem;
        if (sourceItem is not null)
        {
            ShowDragGhost(sourceItem, e.GetPosition(sourceItem));
        }

        var groups = _viewModel.Groups;
        BeginReorderPreview(
            groups.Count,
            groups.IndexOf(_groupDragSource),
            i => GroupsList.ItemContainerGenerator.ContainerFromIndex(i) is { } container
                ? FindVisualChild<Border>(container, "Bd")
                : null);

        try
        {
            DragDrop.DoDragDrop(GroupsList, data, DragDropEffects.Move);
        }
        finally
        {
            HideDragGhost();
            EndReorderPreview();
            ClearGroupDropIndicator();
            _activeGroupInsertAt = null;
            _groupDragSource = null;
            _groupDragSourceItem = null;
        }
    }

    private void GroupsList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(GroupRowDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        UpdateDragGhost(e.GetPosition(this));

        var (item, isBelow) = ResolveGroupDropTarget(e);
        if (item is null)
        {
            ClearGroupDropIndicator();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Slide siblings to show where the ghost would land; the drop bar is redundant now.
        if (item.DataContext is GroupViewModel targetGroup)
        {
            var targetIdx = _viewModel.Groups.IndexOf(targetGroup);
            if (targetIdx >= 0)
            {
                var insertAt = isBelow ? targetIdx + 1 : targetIdx;
                _activeGroupInsertAt = insertAt;
                UpdateReorderPreview(insertAt);
            }
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    // Returns the ListViewItem the drop line should attach to, and whether the line should render
    // on its bottom edge (true only when dropping after the final item).
    private (System.Windows.Controls.ListViewItem? Item, bool IsBelow) ResolveGroupDropTarget(DragEventArgs e)
    {
        if (_viewModel.Groups.Count == 0) return (null, false);

        var listPos = e.GetPosition(GroupsList);
        System.Windows.Controls.ListViewItem? last = null;

        for (var i = 0; i < _viewModel.Groups.Count; i++)
        {
            if (GroupsList.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.ListViewItem container) continue;
            last = container;

            var topLeft = container.TranslatePoint(new Point(0, 0), GroupsList);
            var mid = topLeft.Y + container.ActualHeight / 2;
            if (listPos.Y < mid)
            {
                return (container, false);
            }
        }

        // Past every item's midpoint — drop after the last one.
        return (last, true);
    }

    private void GroupsList_Drop(object sender, DragEventArgs e)
    {
        ClearGroupDropIndicator();

        if (e.Data.GetData(GroupRowDragFormat) is not GroupViewModel source) return;

        // Prefer the insertion index captured during DragOver; re-resolving at drop time would
        // walk un-translated row positions and disagree with the visual preview the user sees.
        if (_activeGroupInsertAt is not int insertAt)
        {
            _viewModel.ReorderGroup(source, _viewModel.Groups.Count - 1);
            e.Handled = true;
            return;
        }

        var sourceIdx = _viewModel.Groups.IndexOf(source);
        if (sourceIdx >= 0 && sourceIdx < insertAt) insertAt--;

        _viewModel.ReorderGroup(source, insertAt);
        e.Handled = true;
    }

    private void ClearGroupDropIndicator()
    {
        if (_activeGroupDropItem is null) return;
        SetGroupDropVisible(_activeGroupDropItem, above: true, visible: false);
        SetGroupDropVisible(_activeGroupDropItem, above: false, visible: false);
        _activeGroupDropItem = null;
    }

    private static void SetGroupDropVisible(System.Windows.Controls.ListViewItem item, bool above, bool visible)
    {
        var name = above ? "GroupDropAbove" : "GroupDropBelow";
        if (FindVisualChild<System.Windows.Shapes.Rectangle>(item, name) is { } rect)
        {
            rect.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
            {
                return fe;
            }
            if (FindVisualChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T)
        {
            d = VisualTreeHelper.GetParent(d);
        }
        return d as T;
    }

}
