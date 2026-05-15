using CommunityToolkit.Mvvm.ComponentModel;
using Salvo.Core.Models.Flow;

namespace Salvo.App.ViewModels.Flow;

/// <summary>
/// Editor counterpart of <see cref="Edge"/>. Mostly inert in Phase A2 —
/// the Simple view doesn't render edges directly (the topology is
/// implicit in stage layout). The Flow view renders connector lines
/// from these instances.
/// </summary>
public sealed partial class EdgeViewModel : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _from = string.Empty;
    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string? _label;

    public Edge ToModel() => new() { Id = Id, From = From, To = To, Label = Label };

    public static EdgeViewModel FromModel(Edge e) => new()
    {
        Id = e.Id, From = e.From, To = e.To, Label = e.Label,
    };
}
