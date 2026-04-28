using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartupGroups.Installer.UI.ViewModels;

public enum InstallerStep
{
    Welcome,
    Customize,
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
    public CustomizeViewModel Customize { get; }
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
        Customize = new CustomizeViewModel();
        License = new LicenseViewModel();
        Progress = new ProgressViewModel();
        Success = new SuccessViewModel();

        Welcome.NextRequested += (_, _) => Show(InstallerStep.License);
        Welcome.CustomizeRequested += (_, _) => Show(InstallerStep.Customize);
        Welcome.CancelRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        Customize.BackRequested += (_, _) => Show(InstallerStep.Welcome);
        Customize.CancelRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Customize.ContinueRequested += (_, _) => Show(InstallerStep.License);

        License.BackRequested += (_, _) =>
            Show(_cameViaCustomize ? InstallerStep.Customize : InstallerStep.Welcome);
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

    private bool _cameViaCustomize;

    public void Show(InstallerStep step)
    {
        // Track whether the user routed through Customize so License → Back
        // returns to the right place.
        if (step == InstallerStep.Customize) _cameViaCustomize = true;
        else if (step == InstallerStep.Welcome) _cameViaCustomize = false;

        CurrentStep = step;
        CurrentScreen = step switch
        {
            InstallerStep.Welcome => Welcome,
            InstallerStep.Customize => Customize,
            InstallerStep.License => License,
            InstallerStep.Progress => Progress,
            InstallerStep.Success => Success,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
        };
    }
}
