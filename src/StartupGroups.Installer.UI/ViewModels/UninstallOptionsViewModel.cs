using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StartupGroups.Installer.UI.ViewModels;

/// <summary>
/// Shown when the bundle is invoked with <c>LaunchAction.Uninstall</c> (the
/// user clicked Uninstall in Add/Remove Programs). Lets them opt into wiping
/// user-data folders the MSI doesn't manage:
/// <list type="bullet">
///   <item>Roaming config (<c>%AppData%\StartupGroups</c>): groups, settings.</item>
///   <item>Local data (<c>%LocalAppData%\StartupGroups</c>): logs, feed cache,
///         launch-benchmarks DB.</item>
/// </list>
/// Both default off so a casual uninstall preserves user state across reinstalls;
/// the BA reads these flags after a successful uninstall and deletes the folders.
/// </summary>
public sealed partial class UninstallOptionsViewModel : ObservableObject
{
    [ObservableProperty] private bool _deleteUserConfig;
    [ObservableProperty] private bool _deleteLogsAndCache;

    public event EventHandler? ContinueRequested;
    public event EventHandler? CancelRequested;

    [RelayCommand]
    private void Continue() => ContinueRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
