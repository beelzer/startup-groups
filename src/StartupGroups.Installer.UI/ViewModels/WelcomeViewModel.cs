using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupGroups.Core.Branding;

namespace StartupGroups.Installer.UI.ViewModels;

public sealed partial class WelcomeViewModel : ObservableObject
{
    public string AppName => AppBranding.AppName;
    public string Version => AppBranding.Version;

    public event EventHandler? NextRequested;
    public event EventHandler? CustomizeRequested;
    public event EventHandler? CancelRequested;

    [RelayCommand]
    private void Next() => NextRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Customize() => CustomizeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
