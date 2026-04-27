using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Converters;

public sealed class RunningStateAppearanceConverter : IValueConverter
{
    public ControlAppearance RunningAppearance { get; set; } = ControlAppearance.Primary;

    public ControlAppearance StoppedAppearance { get; set; } = ControlAppearance.Primary;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isRunning = value is bool b && b;
        return isRunning ? RunningAppearance : StoppedAppearance;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
