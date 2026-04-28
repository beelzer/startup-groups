using CommunityToolkit.Mvvm.ComponentModel;

namespace StartupGroups.Installer.UI.ViewModels;

/// <summary>
/// Engine-driven. The BA pushes <see cref="Progress"/>, <see cref="Status"/>,
/// and <see cref="HasFailed"/> from cache/execute event handlers. No user
/// commands here — Phase 3a deliberately doesn't expose Cancel during Apply
/// (the engine's <c>OnExecutePackageBegin</c> cancellation surface needs
/// careful UI handling we'll add in Phase 3c).
/// </summary>
public sealed partial class ProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _status = "Preparing…";
    [ObservableProperty] private string _currentOperation = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private string _failureMessage = string.Empty;
}
