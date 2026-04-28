using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartupGroups.Installer.UI.ViewModels;

public enum InstallerStep
{
    Welcome,
    License,
    Progress,
    Success,
}

/// <summary>
/// Orchestrates the four-screen flow: Welcome → License → Progress → Success.
/// Each screen has its own child view-model; the bound <see cref="CurrentScreen"/>
/// switches as the user advances or the engine fires <c>ApplyComplete</c>.
///
/// The BA subscribes to <see cref="InstallRequested"/>, <see cref="CloseRequested"/>,
/// and <see cref="LaunchAppRequested"/> to bridge UI gestures into engine calls.
/// We deliberately don't reference <c>WixToolset.BootstrapperApplicationApi</c>
/// from the view-model so screen DataTemplates can be authored in pure WPF.
/// </summary>
public sealed partial class InstallerWindowViewModel : ObservableObject
{
    public WelcomeViewModel Welcome { get; }
    public LicenseViewModel License { get; }
    public ProgressViewModel Progress { get; }
    public SuccessViewModel Success { get; }

    [ObservableProperty] private InstallerStep _currentStep = InstallerStep.Welcome;
    [ObservableProperty] private ObservableObject _currentScreen = null!;

    public event EventHandler? InstallRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? LaunchAppRequested;

    public InstallerWindowViewModel()
    {
        Welcome = new WelcomeViewModel();
        License = new LicenseViewModel();
        Progress = new ProgressViewModel();
        Success = new SuccessViewModel();

        Welcome.NextRequested += (_, _) => Show(InstallerStep.License);
        Welcome.CancelRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        License.BackRequested += (_, _) => Show(InstallerStep.Welcome);
        License.CancelRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        License.InstallRequested += (_, _) =>
        {
            Show(InstallerStep.Progress);
            InstallRequested?.Invoke(this, EventArgs.Empty);
        };

        Success.LaunchRequested += (_, _) =>
        {
            LaunchAppRequested?.Invoke(this, EventArgs.Empty);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        Success.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        Show(InstallerStep.Welcome);
    }

    public void Show(InstallerStep step)
    {
        CurrentStep = step;
        CurrentScreen = step switch
        {
            InstallerStep.Welcome => Welcome,
            InstallerStep.License => License,
            InstallerStep.Progress => Progress,
            InstallerStep.Success => Success,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
        };
    }
}
