using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace StartupGroups.App.Controls;

// Floating translucent snapshot of a source visual, drawn on an AdornerLayer above the window.
// Uses a frozen RenderTargetBitmap so the ghost stays visible even when the source element is
// hidden in place during a reorder preview (a VisualBrush would follow the source's opacity).
internal sealed class DragGhostAdorner : Adorner
{
    private readonly Image _ghost;
    private readonly TranslateTransform _translate = new();
    private readonly double _anchorX;
    private readonly double _anchorY;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source, Point grabPoint)
        : base(adornedElement)
    {
        IsHitTestVisible = false;

        var width = source.ActualWidth;
        var height = source.ActualHeight;

        // Snapshot the source once at drag start. DPI comes from the element's own render target.
        var dpi = VisualTreeHelper.GetDpi(source);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);

        // Route through a DrawingVisual + VisualBrush so the snapshot always renders at (0,0),
        // regardless of where `source` sits in its parent's layout — RenderTargetBitmap.Render
        // of a lower-positioned ListViewItem would otherwise draw offset-then-clip in the bitmap.
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var brush = new VisualBrush(source)
            {
                Stretch = Stretch.None,
                AutoLayoutContent = false,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(drawing);
        bitmap.Freeze();

        _ghost = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Opacity = 0.85,
            RenderTransform = _translate,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.45,
            },
        };

        _anchorX = grabPoint.X;
        _anchorY = grabPoint.Y;

        AddVisualChild(_ghost);
        AddLogicalChild(_ghost);
    }

    public void UpdatePosition(Point cursorInAdornerCoords)
    {
        _translate.X = cursorInAdornerCoords.X - _anchorX;
        _translate.Y = cursorInAdornerCoords.Y - _anchorY;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _ghost;

    protected override Size MeasureOverride(Size constraint)
    {
        _ghost.Measure(constraint);
        return _ghost.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ghost.Arrange(new Rect(_ghost.DesiredSize));
        return finalSize;
    }
}
