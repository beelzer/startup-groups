using System;
using System.Windows.Threading;
using StartupGroups.Installer.UI.ViewModels;
using StartupGroups.Installer.UI.Views;
using WixToolset.BootstrapperApplicationApi;
using StartupEventArgs = WixToolset.BootstrapperApplicationApi.StartupEventArgs;
using ShutdownEventArgs = WixToolset.BootstrapperApplicationApi.ShutdownEventArgs;

namespace StartupGroups.Installer.UI;

/// <summary>
/// Phase 3a managed BA — minimum viable wiring. Drives Detect → Plan → Apply
/// and shuts down the WPF dispatcher when ApplyComplete fires. The BA runs
/// as its own process under Burn (WiX 5 OOP model), so we own the entire
/// process lifetime.
///
/// Threading model:
/// - <see cref="Run"/> is invoked on the BA's main thread, which is STA
///   thanks to <c>[STAThread]</c> on Program.Main. We capture
///   <see cref="Dispatcher.CurrentDispatcher"/> there and run the WPF
///   message loop on it.
/// - Burn raises engine events on its own worker thread; UI mutations
///   marshal back via <see cref="_uiDispatcher"/>.
/// - The protected <c>engine</c> field becomes valid after
///   <see cref="OnCreate(CreateEventArgs)"/> fires (Burn calls it during
///   <see cref="ManagedBootstrapperApplication.Run"/>'s connect handshake,
///   before our <see cref="Run"/> override is invoked).
/// </summary>
public sealed class InstallerBootstrapperApplication : BootstrapperApplication
{
    private InstallerWindowViewModel? _viewModel;
    private InstallerWindow? _window;
    private Dispatcher? _uiDispatcher;
    private IBootstrapperCommand? _command;

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        // CreateEventArgs.Command is what tells us Install vs Uninstall vs
        // Repair vs Modify. We can't get this any other way after Run() —
        // Burn ferries it in here exactly once.
        _command = args.Command;
    }

    protected override void Run()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _viewModel = new InstallerWindowViewModel();
        _window = new InstallerWindow(_viewModel);

        _window.Closed += (_, _) => _uiDispatcher.InvokeShutdown();

        DetectComplete += OnDetectComplete;
        PlanComplete += OnPlanComplete;
        ApplyComplete += OnApplyComplete;
        Error += OnError;
        ExecuteProgress += OnExecuteProgress;
        CacheAcquireProgress += OnCacheAcquireProgress;

        engine.Log(LogLevel.Standard, $"Phase 3a BA: Run() entered, action={_command?.Action}");
        engine.Detect();

        _window.Show();
        Dispatcher.Run();

        // Dispatcher.Run returned because InvokeShutdown was called above
        // (window closed). Tell the engine we're done; the OOP host process
        // exits afterwards.
        engine.Quit(0);
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        engine.Log(LogLevel.Standard, "Phase 3a BA: OnStartup");
    }

    protected override void OnShutdown(ShutdownEventArgs args)
    {
        base.OnShutdown(args);
        engine.Log(LogLevel.Standard, $"Phase 3a BA: OnShutdown action={args.Action} hr=0x{args.HResult:X8}");
    }

    // === Engine event handlers (engine worker thread) ===

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            engine.Log(LogLevel.Error, $"Detect failed status=0x{e.Status:X8}");
            ShutdownUi();
            return;
        }

        // Forward Burn's intent. Phase 3c adds a "What would you like to
        // do?" branch when the user re-launches Setup.exe on an installed
        // system; for now Burn already handles that via the Action field
        // (e.g. Modify when invoked from Programs and Features).
        var action = _command?.Action ?? LaunchAction.Install;
        engine.Plan(action);
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs e)
    {
        if (e.Status < 0)
        {
            engine.Log(LogLevel.Error, $"Plan failed status=0x{e.Status:X8}");
            ShutdownUi();
            return;
        }

        UpdateUi(vm => vm.Status = "Installing…");
        engine.Apply(IntPtr.Zero);
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        engine.Log(LogLevel.Standard, $"ApplyComplete status=0x{e.Status:X8}");
        UpdateUi(vm =>
        {
            if (e.Status >= 0)
            {
                vm.Status = "Done.";
                vm.Progress = 1.0;
            }
            else
            {
                vm.Status = $"Install failed (0x{e.Status:X8}). See log for details.";
                vm.HasFailed = true;
            }
        });

        // Phase 3a: auto-close on success. Phase 3c keeps the Success
        // screen up with a "Launch StartupGroups" button.
        if (e.Status >= 0)
        {
            ShutdownUi();
        }
    }

    private void OnError(object? sender, ErrorEventArgs e)
    {
        engine.Log(LogLevel.Error, $"Error code={e.ErrorCode} message={e.ErrorMessage}");
        UpdateUi(vm =>
        {
            vm.HasFailed = true;
            vm.Status = e.ErrorMessage ?? "Unknown error";
        });
        e.Result = Result.None;
    }

    private void OnExecuteProgress(object? sender, ExecuteProgressEventArgs e)
    {
        UpdateUi(vm => vm.Progress = e.OverallPercentage / 100.0);
    }

    private void OnCacheAcquireProgress(object? sender, CacheAcquireProgressEventArgs e)
    {
        if (e.Total > 0)
        {
            // Cache phase is ~30% of the work for an MSI install.
            var ratio = (double)e.Progress / e.Total;
            UpdateUi(vm => vm.Progress = ratio * 0.3);
        }
    }

    // === UI helpers ===

    private void UpdateUi(Action<InstallerWindowViewModel> mutate)
    {
        if (_uiDispatcher is null || _viewModel is null) return;
        _uiDispatcher.BeginInvoke(() => mutate(_viewModel));
    }

    private void ShutdownUi()
    {
        // Marshal to the UI thread so the dispatcher tears down cleanly.
        _uiDispatcher?.BeginInvoke(() => _window?.Close());
    }
}
