using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StartupGroups.App.Converters;

public sealed class FractionToWidthConverter : IValueConverter
{
    public double ScalePx { get; set; } = 520;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var scale = ResolveScale(parameter);
        if (value is double d) return Math.Max(1.0, d * scale);
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private double ResolveScale(object? parameter)
    {
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return ScalePx;
    }
}

public sealed class FractionToLeftMarginConverter : IValueConverter
{
    public double ScalePx { get; set; } = 520;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var scale = ResolveScale(parameter);
        if (value is double d) return new Thickness(d * scale, 0, 0, 0);
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private double ResolveScale(object? parameter)
    {
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return ScalePx;
    }
}
