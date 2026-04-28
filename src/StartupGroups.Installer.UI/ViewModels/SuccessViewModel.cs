using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupGroups.Core.Branding;

namespace StartupGroups.Installer.UI.ViewModels;

public sealed partial class SuccessViewModel : ObservableObject
{
    public string AppName => AppBranding.AppName;

    public event EventHandler? LaunchRequested;
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Launch() => LaunchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
