using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Converters;

public sealed class StringToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && Enum.TryParse<SymbolRegular>(s, ignoreCase: false, out var symbol))
        {
            return symbol;
        }
        return SymbolRegular.Apps24;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
