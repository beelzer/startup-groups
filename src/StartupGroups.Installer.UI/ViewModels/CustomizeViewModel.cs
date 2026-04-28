using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StartupGroups.Installer.UI.ViewModels;

public enum InstallerUpdateChannel
{
    Stable,
    Beta,
    Nightly,
}

/// <summary>
/// Optional second screen reachable from Welcome via "Customize…". Lets the
/// user pick the update channel and opt into auto-start before installation
/// begins. The choices are persisted to <c>%APPDATA%\StartupGroups\settings.json</c>
/// after Apply succeeds; auto-start is forwarded to the launched app via
/// <c>--enable-autostart</c> on first launch.
///
/// We deliberately don't expose an editable install path — the chained MSI
/// defines its own INSTALLFOLDER and Burn doesn't make redirecting it cheap.
/// Phase 4 can revisit if the per-machine directory choice becomes friction.
/// </summary>
public sealed partial class CustomizeViewModel : ObservableObject
{
    public string InstallPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Startup Groups");

    public IReadOnlyList<InstallerUpdateChannel> AvailableChannels { get; } =
    [
        InstallerUpdateChannel.Stable,
        InstallerUpdateChannel.Beta,
        InstallerUpdateChannel.Nightly,
    ];

    [ObservableProperty] private InstallerUpdateChannel _selectedChannel = InstallerUpdateChannel.Stable;
    [ObservableProperty] private bool _enableAutoStart;

    public event EventHandler? ContinueRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? CancelRequested;

    [RelayCommand]
    private void Continue() => ContinueRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
