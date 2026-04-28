using CommunityToolkit.Mvvm.ComponentModel;

namespace StartupGroups.Installer.UI.ViewModels;

/// <summary>
/// Phase 3a — single-page progress view-model. Phase 3b adds per-screen
/// view-models for Welcome / License / Customize / Success.
/// </summary>
public sealed partial class InstallerWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _status = "Preparing…";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasFailed;
}
