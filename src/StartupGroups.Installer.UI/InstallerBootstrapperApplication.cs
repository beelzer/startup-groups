using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using StartupGroups.Installer.UI.ViewModels;
using StartupGroups.Installer.UI.Views;
using WixToolset.BootstrapperApplicationApi;
using Wpf.Ui.Appearance;
using StartupEventArgs = WixToolset.BootstrapperApplicationApi.StartupEventArgs;
using ShutdownEventArgs = WixToolset.BootstrapperApplicationApi.ShutdownEventArgs;
using ErrorEventArgs = WixToolset.BootstrapperApplicationApi.ErrorEventArgs;

namespace StartupGroups.Installer.UI;

/// <summary>
/// Phase 3b managed BA. Drives Detect → Plan → Apply on the engine thread,
/// runs the four-screen WPF UI on the BA's STA main thread (Welcome →
/// License → Progress → Success), and bridges UI gestures into engine
/// calls.
///
/// Flow:
/// - <see cref="OnCreate"/>: capture the IBootstrapperCommand (Install vs
///   Uninstall vs Repair). Burn calls this exactly once during the OOP
///   handshake, before <see cref="Run"/>.
/// - <see cref="Run"/>: own the dispatcher, show the window on Welcome,
///   subscribe to UI events, fire <c>engine.Detect()</c> in the background
///   so it's done by the time the user clicks Install on the License screen.
/// - User clicks Install → <see cref="OnInstallRequested"/> → engine.Plan,
///   then engine.Apply chained from PlanComplete.
/// - ApplyComplete → switch the orchestrator to the Success screen (or
///   stay on Progress with an error message).
/// - User clicks Launch on Success → spawn StartupGroups.exe via
///   <c>InstalledLocation</c> registry lookup; falls back to a known
///   Program Files path if the registry lookup fails.
/// </summary>
public sealed class InstallerBootstrapperApplication : BootstrapperApplication
{
    private InstallerWindowViewModel? _viewModel;
    private InstallerWindow? _window;
    private App? _wpfApp;
    private Dispatcher? _uiDispatcher;
    private IBootstrapperCommand? _command;
    private readonly object _stateGate = new();
    private bool _detectComplete;
    private bool _installRequested;

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        _command = args.Command;
    }

    protected override void Run()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        // Create + initialize a WPF Application so App.xaml's resource
        // dictionaries (WPF-UI themes, control styles) are merged into the
        // global resource scope. Without this, Mica + accent brushes resolve
        // to defaults and dark-mode following doesn't kick in.
        _wpfApp = new App();
        _wpfApp.InitializeComponent();
        // OnStartup never fires (we use Dispatcher.Run, not Application.Run),
        // so apply the system theme + accent here explicitly. Mica chrome
        // already comes from FluentWindow's WindowBackdropType="Mica" — this
        // call updates the resource brushes to match the OS personalization
        // (light/dark + accent).
        ApplicationThemeManager.ApplySystemTheme(updateAccent: true);

        _viewModel = new InstallerWindowViewModel();
        _window = new InstallerWindow(_viewModel);

        _viewModel.InstallRequested += OnInstallRequested;
        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.LaunchAppRequested += OnLaunchAppRequested;
        _window.Closed += (_, _) => _uiDispatcher.InvokeShutdown();

        DetectComplete += OnDetectComplete;
        PlanComplete += OnPlanComplete;
        ApplyComplete += OnApplyComplete;
        Error += OnError;
        ExecuteProgress += OnExecuteProgress;
        CacheAcquireProgress += OnCacheAcquireProgress;

        var action = _command?.Action ?? LaunchAction.Install;
        engine.Log(LogLevel.Standard, $"Phase 3c BA: Run() entered, action={action}");

        // For non-Install invocations (Burn re-runs us for Uninstall via
        // Add/Remove Programs, or Modify/Repair from the bundle), skip
        // Welcome / Customize / License and go straight to Progress. The
        // license has already been accepted on the original install; Modify
        // and Uninstall don't need it again.
        if (action != LaunchAction.Install)
        {
            _viewModel.Progress.Status = action switch
            {
                LaunchAction.Uninstall => "Uninstalling…",
                LaunchAction.Repair => "Repairing…",
                LaunchAction.Modify => "Updating…",
                _ => "Working…",
            };
            _viewModel.Show(InstallerStep.Progress);
            // Mark "user has consented" so OnDetectComplete drives Plan
            // automatically without waiting for an Install button click.
            lock (_stateGate) { _installRequested = true; }
        }

        // Detect runs in the background while the user reads Welcome/License.
        // It's fast (typically <100ms), so by the time they click Install
        // on the License screen, we already know the install state.
        engine.Detect();

        _window.Show();
        Dispatcher.Run();

        engine.Quit(0);
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        engine.Log(LogLevel.Standard, "Phase 3b BA: OnStartup");
    }

    protected override void OnShutdown(ShutdownEventArgs args)
    {
        base.OnShutdown(args);
        engine.Log(LogLevel.Standard, $"Phase 3b BA: OnShutdown action={args.Action} hr=0x{args.HResult:X8}");
    }

    // === UI events (UI thread) ===

    private void OnInstallRequested(object? sender, EventArgs e)
    {
        // The user clicked Install on the License screen. Detect may have
        // already finished (it's been running since Run()) or may still be
        // in flight. Take the lock; if Detect is done, start Plan now,
        // otherwise OnDetectComplete will pick up _installRequested when it
        // fires. Either ordering produces exactly one BeginPlan().
        bool startNow;
        lock (_stateGate)
        {
            _installRequested = true;
            startNow = _detectComplete;
        }

        UpdateUi(vm =>
        {
            vm.Progress.Status = "Preparing install…";
            vm.Progress.Progress = 0;
        });

        if (startNow)
        {
            BeginPlan();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _uiDispatcher?.BeginInvoke(() => _window?.Close());
    }

    private void OnLaunchAppRequested(object? sender, EventArgs e)
    {
        var appPath = ResolveInstalledAppPath();
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
        {
            engine.Log(LogLevel.Error, $"Launch requested but app not found at {appPath}");
            return;
        }

        // Forward the auto-start choice from Customize. The app's
        // TaskSchedulerAutoStartService picks this up in App.OnStartup
        // and registers the scheduled task on first launch only.
        var enableAutoStart = _viewModel?.Customize.EnableAutoStart == true;
        var args = enableAutoStart ? "--enable-autostart" : string.Empty;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                Arguments = args,
                UseShellExecute = true,
            });
            engine.Log(LogLevel.Standard, $"Launched {appPath} {args}".TrimEnd());
        }
        catch (Exception ex)
        {
            engine.Log(LogLevel.Error, $"Failed to launch {appPath}: {ex.Message}");
        }
    }

    private static string? ResolveInstalledAppPath()
    {
        // The MSI installs to %ProgramFiles%\Startup Groups\StartupGroups.exe
        // (per the Package.wxs ProductName attribute). Burn's per-machine
        // install honours x64; check that first, fall back to per-user.
        var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(pf64, "Startup Groups", "StartupGroups.exe");
        if (File.Exists(candidate)) return candidate;

        var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidate = Path.Combine(pfX86, "Startup Groups", "StartupGroups.exe");
        if (File.Exists(candidate)) return candidate;

        return null;
    }

    // === Engine event handlers (engine worker thread) ===

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            engine.Log(LogLevel.Error, $"Detect failed status=0x{e.Status:X8}");
            UpdateUi(vm =>
            {
                vm.Progress.HasFailed = true;
                vm.Progress.FailureMessage = $"Detect failed (0x{e.Status:X8}).";
                vm.Show(InstallerStep.Progress);
            });
            return;
        }

        // Detect finished. If the user has already clicked Install on the
        // License screen, drive Plan now. Otherwise mark detect-done and
        // OnInstallRequested will start Plan once it arrives.
        bool startNow;
        lock (_stateGate)
        {
            _detectComplete = true;
            startNow = _installRequested;
        }

        if (startNow)
        {
            BeginPlan();
        }
    }

    private void BeginPlan()
    {
        var action = _command?.Action ?? LaunchAction.Install;
        engine.Log(LogLevel.Standard, $"Phase 3b BA: Plan({action})");
        engine.Plan(action);
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            engine.Log(LogLevel.Error, $"Plan failed status=0x{e.Status:X8}");
            UpdateUi(vm =>
            {
                vm.Progress.HasFailed = true;
                vm.Progress.FailureMessage = $"Plan failed (0x{e.Status:X8}).";
            });
            return;
        }

        UpdateUi(vm => vm.Progress.Status = "Installing…");
        engine.Apply(IntPtr.Zero);
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        engine.Log(LogLevel.Standard, $"ApplyComplete status=0x{e.Status:X8}");

        if (e.Status >= 0)
        {
            // Write the user's chosen update channel into the per-user
            // settings.json before the app first launches. We only write if
            // the file doesn't already exist — preserves any prior config
            // (re-install / upgrade).
            TrySeedFirstRunSettings();
        }

        UpdateUi(vm =>
        {
            if (e.Status >= 0)
            {
                vm.Progress.Status = "Done.";
                vm.Progress.Progress = 1.0;
                vm.Show(InstallerStep.Success);
            }
            else
            {
                vm.Progress.HasFailed = true;
                vm.Progress.FailureMessage = $"Install failed (0x{e.Status:X8}). Check the log for details.";
                vm.Progress.Status = "Failed.";
            }
        });
    }

    private void TrySeedFirstRunSettings()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "StartupGroups");
            var settingsPath = Path.Combine(dir, "settings.json");
            if (File.Exists(settingsPath))
            {
                engine.Log(LogLevel.Standard, "settings.json already present; skipping seed.");
                return;
            }

            Directory.CreateDirectory(dir);

            // Mirror the App's AppSettings shape: camelCase, string enum.
            // Only seed the fields the user explicitly chose; all other
            // properties fall back to their defaults when the app reads
            // settings.json on first launch.
            var channel = _viewModel?.Customize.SelectedChannel switch
            {
                InstallerUpdateChannel.Beta => "Beta",
                InstallerUpdateChannel.Nightly => "Nightly",
                _ => "Stable",
            };
            var json = $"{{\n  \"updateChannel\": \"{channel}\"\n}}\n";
            File.WriteAllText(settingsPath, json);
            engine.Log(LogLevel.Standard, $"Seeded settings.json with channel={channel}");
        }
        catch (Exception ex)
        {
            engine.Log(LogLevel.Error, $"Failed to seed settings.json: {ex.Message}");
        }
    }

    private void OnError(object? sender, ErrorEventArgs e)
    {
        engine.Log(LogLevel.Error, $"Error code={e.ErrorCode} message={e.ErrorMessage}");
        UpdateUi(vm =>
        {
            vm.Progress.HasFailed = true;
            vm.Progress.FailureMessage = e.ErrorMessage ?? $"Error 0x{e.ErrorCode:X8}";
        });
        e.Result = Result.None;
    }

    private void OnExecuteProgress(object? sender, ExecuteProgressEventArgs e)
    {
        UpdateUi(vm => vm.Progress.Progress = e.OverallPercentage / 100.0);
    }

    private void OnCacheAcquireProgress(object? sender, CacheAcquireProgressEventArgs e)
    {
        if (e.Total > 0)
        {
            // Cache phase is ~30% of total work for an MSI install.
            var ratio = (double)e.Progress / e.Total;
            UpdateUi(vm => vm.Progress.Progress = ratio * 0.3);
        }
    }

    // === UI helpers ===

    private void UpdateUi(Action<InstallerWindowViewModel> mutate)
    {
        if (_uiDispatcher is null || _viewModel is null) return;
        var vm = _viewModel;
        _uiDispatcher.BeginInvoke(() => mutate(vm));
    }
}
