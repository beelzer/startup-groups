using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupGroups.Core.Branding;

namespace StartupGroups.Installer.UI.ViewModels;

public sealed partial class SuccessViewModel : ObservableObject
{
    public string AppName => AppBranding.AppName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    private bool _isUpgrade;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    private bool _isUninstall;

    public string Title => IsUninstall
        ? "Uninstall complete"
        : IsUpgrade ? "Update complete" : "Install complete";

    public string Subtitle => IsUninstall
        ? $"{AppName} has been removed from this PC."
        : $"{AppName} is ready to use.";

    public bool ShowLaunchButton => !IsUninstall;

    public event EventHandler? LaunchRequested;
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Launch() => LaunchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
