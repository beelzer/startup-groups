using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.App.Resources;
using StartupGroups.Core.Models;

namespace StartupGroups.App.ViewModels;

public partial class InstalledAppViewModel : ObservableObject
{
    public InstalledAppViewModel(InstalledApp model)
    {
        Model = model;
    }

    public InstalledApp Model { get; }

    public string Name => Model.Name;
    public string? ExecutablePath => Model.ExecutablePath;
    public string Launch => Model.Launch;
    public InstalledAppSource Source => Model.Source;

    public string SubtitleText => Model.ExecutablePath ?? Model.Launch;

    public string SourceBadge => Model.Source switch
    {
        InstalledAppSource.Uwp => Strings.AddAppPicker_SourceStore,
        InstalledAppSource.Desktop => Strings.AddAppPicker_SourceDesktop,
        InstalledAppSource.Service => Strings.AddAppPicker_SourceService,
        InstalledAppSource.Scoop => Strings.AddAppPicker_SourceScoop,
        _ => Strings.AddAppPicker_SourceShell,
    };

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private BitmapSource? _icon;
}
