using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.App.Resources;
using StartupGroups.Core.Launch;
using StartupGroups.Core.WindowsStartup;

namespace StartupGroups.App.ViewModels;

public partial class WindowsStartupEntryViewModel : ObservableObject
{
    public WindowsStartupEntryViewModel(WindowsStartupEntry model)
    {
        Model = model;
        _enabled = model.Enabled;
    }

    public WindowsStartupEntry Model { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private BitmapSource? _icon;

    public string Name => Model.Name;

    public string Command => Model.Command;

    public string Source => Model.SourceShortLabel;

    public string? SourceDescription => Model.SourceDescription;

    // HKLM / Common-startup-folder entries need admin. When the app is already elevated,
    // those writes succeed in-process, so the row is editable. Otherwise we disable the
    // controls and surface a tooltip explaining why.
    public bool CanModify => Model.CanModifyWithoutAdmin || ElevationDetector.IsElevated;

    public bool RequiresAdmin => !CanModify;

    public object? AdminRequiredTooltip => RequiresAdmin ? Strings.WinStartup_RequiresAdmin : null;

    public string RemoveTooltip => RequiresAdmin ? Strings.WinStartup_RequiresAdmin : Strings.Action_Remove;

    // Drives icon/tooltip on the row's "open" button: registry-sourced rows open the
    // in-app editor (pencil), folder-sourced rows reveal the file in Explorer (folder).
    public bool IsRegistryEntry => Model.Source
        is StartupEntrySource.RegistryRunUser
        or StartupEntrySource.RegistryRunUser32
        or StartupEntrySource.RegistryRunMachine
        or StartupEntrySource.RegistryRunMachine32;

    public string OpenActionTooltip => IsRegistryEntry
        ? Strings.WinStartup_EditRegistryValue
        : Strings.Action_OpenLocation;
}
