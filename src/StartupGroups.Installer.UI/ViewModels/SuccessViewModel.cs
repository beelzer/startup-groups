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
    private bool _isUpgrade;

    public string Title => IsUpgrade ? "Update complete" : "Install complete";

    public event EventHandler? LaunchRequested;
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Launch() => LaunchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
