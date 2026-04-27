using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StartupGroups.App.Services;

namespace StartupGroups.App.Controls;

[SupportedOSPlatform("windows")]
public partial class GroupIconView : UserControl
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(GroupIconView),
            new PropertyMetadata("Apps24", OnIconChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(GroupIconView),
            new PropertyMetadata(20.0, OnSizeChanged));

    public static readonly DependencyProperty IconForegroundProperty =
        DependencyProperty.Register(
            nameof(IconForeground),
            typeof(Brush),
            typeof(GroupIconView),
            new PropertyMetadata(null, OnForegroundChanged));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Brush? IconForeground
    {
        get => (Brush?)GetValue(IconForegroundProperty);
        set => SetValue(IconForegroundProperty, value);
    }

    public GroupIconView()
    {
        InitializeComponent();
        Refresh();
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GroupIconView)d).Refresh();

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GroupIconView)d).Refresh();

    private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GroupIconView)d).Refresh();

    private void Refresh()
    {
        var spec = GroupIconSpec.Parse(Icon);
        switch (spec.Kind)
        {
            case GroupIconKind.Stock:
                FluentIcon.Visibility = Visibility.Collapsed;
                StockImage.Visibility = Visibility.Visible;
                StockImage.Source = StockIconExtractor.Get(spec.StockId);
                StockImage.Width = IconSize;
                StockImage.Height = IconSize;
                break;

            case GroupIconKind.App:
                FluentIcon.Visibility = Visibility.Collapsed;
                StockImage.Visibility = Visibility.Visible;
                StockImage.Source = !string.IsNullOrWhiteSpace(spec.AppSource)
                    ? AppIconCache.Get(spec.AppSource!)
                    : null;
                StockImage.Width = IconSize;
                StockImage.Height = IconSize;
                break;

            default:
                StockImage.Visibility = Visibility.Collapsed;
                FluentIcon.Visibility = Visibility.Visible;
                FluentIcon.Symbol = spec.Symbol;
                FluentIcon.Filled = spec.Kind == GroupIconKind.Filled;
                FluentIcon.FontSize = IconSize;
                if (IconForeground is not null)
                {
                    FluentIcon.Foreground = IconForeground;
                }
                break;
        }
    }
}
