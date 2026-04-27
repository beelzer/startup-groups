using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StartupGroups.App.Resources;

namespace StartupGroups.App.Converters;

public sealed class RunningStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isRunning = value is bool b && b;
        var key = isRunning ? "StatusRunningBrush" : "StatusStoppedBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RunningStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isRunning = value is bool b && b;
        return isRunning ? Strings.RunningState_Running : Strings.RunningState_Stopped;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
