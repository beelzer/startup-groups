using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupGroups.App.Services;
using Wpf.Ui.Controls;

namespace StartupGroups.App.ViewModels;

public partial class IconOptionViewModel : ObservableObject
{
    public IconOptionViewModel(string value, string? displayName = null)
    {
        Value = value;
        DisplayName = displayName;
    }

    public string Value { get; }
    public string? DisplayName { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// A row of icons. ListBox virtualizes rows (not individual icons), so the icon-picker
/// grid only realizes visuals for rows currently in the viewport.
/// </summary>
public sealed class IconRow : List<IconOptionViewModel>
{
    public IconRow(IEnumerable<IconOptionViewModel> items) : base(items) { }
}

public partial class GroupEditorViewModel : ObservableObject
{
    private const int IconsPerRow = 10;

    // Every SymbolRegular enum value whose name ends in the baseline "24" size suffix.
    private static readonly string[] FluentSymbols =
        Enum.GetNames<SymbolRegular>()
            .Where(n => n.EndsWith("24", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    // All Windows shell stock icon IDs (SHSTOCKICONID). Invalid IDs render as blank.
    private static readonly uint[] StockIconIds =
        Enumerable.Range(0, 189).Select(i => (uint)i).ToArray();

    private readonly List<IconOptionViewModel> _allAppIcons = [];
    private readonly List<IconOptionViewModel> _allOutlineIcons;
    private readonly List<IconOptionViewModel> _allFilledIcons;
    private readonly List<IconOptionViewModel> _allStockIcons;

    public GroupEditorViewModel()
    {
        _allOutlineIcons = FluentSymbols.Select(s => new IconOptionViewModel(s, s)).ToList();
        _allFilledIcons = FluentSymbols.Select(s => new IconOptionViewModel("filled:" + s, s)).ToList();
        _allStockIcons = StockIconIds.Select(id => new IconOptionViewModel($"stock:{id}", $"#{id}")).ToList();

        AppIconRows = [];
        OutlineIconRows = new ObservableCollection<IconRow>(ChunkRows(_allOutlineIcons));
        FilledIconRows = new ObservableCollection<IconRow>(ChunkRows(_allFilledIcons));
        StockIconRows = new ObservableCollection<IconRow>(ChunkRows(_allStockIcons));

        RefreshSelection();
    }

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _icon = "Apps24";
    [ObservableProperty] private string _iconSearch = string.Empty;

    public bool IsNew { get; set; } = true;

    public ObservableCollection<IconRow> AppIconRows { get; }
    public ObservableCollection<IconRow> OutlineIconRows { get; }
    public ObservableCollection<IconRow> FilledIconRows { get; }
    public ObservableCollection<IconRow> StockIconRows { get; }

    public bool HasAppIcons => _allAppIcons.Count > 0;

    public void LoadApps(IEnumerable<AppEntryViewModel> apps)
    {
        _allAppIcons.Clear();
        foreach (var app in apps)
        {
            var source = AppIconLoader.ResolveSource(app);
            if (string.IsNullOrWhiteSpace(source)) continue;
            _allAppIcons.Add(new IconOptionViewModel($"app:{source}", app.Name));
        }

        RebuildRows(AppIconRows, _allAppIcons);
        OnPropertyChanged(nameof(HasAppIcons));
        RefreshSelection();
    }

    partial void OnIconChanged(string value) => RefreshSelection();

    partial void OnIconSearchChanged(string value)
    {
        RebuildRows(AppIconRows, _allAppIcons);
        RebuildRows(OutlineIconRows, _allOutlineIcons);
        RebuildRows(FilledIconRows, _allFilledIcons);
        RebuildRows(StockIconRows, _allStockIcons);
    }

    private void RebuildRows(ObservableCollection<IconRow> target, IReadOnlyList<IconOptionViewModel> source)
    {
        target.Clear();
        foreach (var row in ChunkRows(Filter(source)))
        {
            target.Add(row);
        }
    }

    private IEnumerable<IconOptionViewModel> Filter(IEnumerable<IconOptionViewModel> source)
    {
        if (string.IsNullOrWhiteSpace(IconSearch))
        {
            return source;
        }
        var q = IconSearch.Trim();
        return source.Where(o =>
            (o.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || o.Value.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<IconRow> ChunkRows(IEnumerable<IconOptionViewModel> source)
    {
        var row = new List<IconOptionViewModel>(IconsPerRow);
        foreach (var item in source)
        {
            row.Add(item);
            if (row.Count == IconsPerRow)
            {
                yield return new IconRow(row);
                row = new List<IconOptionViewModel>(IconsPerRow);
            }
        }
        if (row.Count > 0)
        {
            yield return new IconRow(row);
        }
    }

    private void RefreshSelection()
    {
        foreach (var option in _allAppIcons
            .Concat(_allOutlineIcons)
            .Concat(_allFilledIcons)
            .Concat(_allStockIcons))
        {
            option.IsSelected = string.Equals(option.Value, Icon, StringComparison.Ordinal);
        }
    }

    [RelayCommand]
    private void SelectIcon(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Icon = value;
        }
    }
}
