using CommunityToolkit.Mvvm.ComponentModel;
using Salvo.Core.Models;

namespace Salvo.App.ViewModels;

/// <summary>
/// One source-type filter chip in the Add-app picker (Desktop, Store,
/// Shell, Scoop, Service). Toggling <see cref="IsEnabled"/> shows or hides
/// that source's apps in the list. Carries the live count for the label.
/// </summary>
public sealed partial class SourceFilterViewModel : ObservableObject
{
    public SourceFilterViewModel(InstalledAppSource source, string label, int count)
    {
        Source = source;
        Label = label;
        Count = count;
    }

    public InstalledAppSource Source { get; }
    public string Label { get; }
    public int Count { get; }

    public string DisplayText => $"{Label} ({Count})";

    [ObservableProperty] private bool _isEnabled = true;
}
