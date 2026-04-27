using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.Core.Launch;

namespace StartupGroups.App.ViewModels;

public partial class DependencyHintViewModel : ObservableObject
{
    [ObservableProperty] private string _groupId = string.Empty;
    [ObservableProperty] private string _currentOrderDisplay = string.Empty;
    [ObservableProperty] private string _suggestedOrderDisplay = string.Empty;
    [ObservableProperty] private int _edgeCount;
    [ObservableProperty] private bool _isReorderSuggested;

    public ObservableCollection<string> EdgeDescriptions { get; } = [];
    public IReadOnlyList<string> SuggestedAppIds { get; private set; } = Array.Empty<string>();

    public static DependencyHintViewModel FromHint(DependencyHint hint)
    {
        var vm = new DependencyHintViewModel
        {
            GroupId = hint.GroupId,
            CurrentOrderDisplay = string.Join(" \u2192 ", hint.CurrentOrder.Select(id => NameFor(hint, id))),
            SuggestedOrderDisplay = string.Join(" \u2192 ", hint.SuggestedOrder.Select(id => NameFor(hint, id))),
            EdgeCount = hint.Edges.Count,
            IsReorderSuggested = hint.IsReorderSuggested,
            SuggestedAppIds = hint.SuggestedOrder,
        };
        foreach (var e in hint.Edges)
        {
            vm.EdgeDescriptions.Add($"{NameFor(hint, e.FromAppId)} \u2192 {NameFor(hint, e.ToAppId)}  (seen {e.Confidence}x)");
        }
        return vm;
    }

    private static string NameFor(DependencyHint hint, string appId) =>
        hint.AppIdToName.TryGetValue(appId, out var n) ? n : appId;
}
