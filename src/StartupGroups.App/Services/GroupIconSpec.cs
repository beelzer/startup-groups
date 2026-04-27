using System;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Services;

public enum GroupIconKind
{
    Outline,
    Filled,
    Stock,
    App
}

public readonly record struct GroupIconSpec(GroupIconKind Kind, SymbolRegular Symbol, uint StockId, string? AppSource)
{
    private const string FilledPrefix = "filled:";
    private const string StockPrefix = "stock:";
    private const string AppPrefix = "app:";

    public static GroupIconSpec Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new GroupIconSpec(GroupIconKind.Outline, SymbolRegular.Apps24, 0, null);
        }

        if (value.StartsWith(FilledPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new GroupIconSpec(GroupIconKind.Filled, ParseSymbol(value[FilledPrefix.Length..]), 0, null);
        }

        if (value.StartsWith(StockPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(value[StockPrefix.Length..], out var parsed)
                ? new GroupIconSpec(GroupIconKind.Stock, SymbolRegular.Apps24, parsed, null)
                : new GroupIconSpec(GroupIconKind.Outline, SymbolRegular.Apps24, 0, null);
        }

        if (value.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var source = value[AppPrefix.Length..];
            return new GroupIconSpec(GroupIconKind.App, SymbolRegular.Apps24, 0, source);
        }

        return new GroupIconSpec(GroupIconKind.Outline, ParseSymbol(value), 0, null);
    }

    private static SymbolRegular ParseSymbol(string name) =>
        Enum.TryParse<SymbolRegular>(name, ignoreCase: false, out var result)
            ? result
            : SymbolRegular.Apps24;
}
