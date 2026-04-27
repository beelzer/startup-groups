using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StartupGroups.App.Converters;

public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var matches = value is not null && parameter is not null
            && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

        if (targetType == typeof(Visibility))
        {
            return matches ? Visibility.Visible : Visibility.Collapsed;
        }
        return matches;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null && targetType.IsEnum)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}
