using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Interop;
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
    private Thread? _uiThread;
    private readonly ManualResetEventSlim _uiReady = new();
    private IBootstrapperCommand? _command;
    private readonly object _stateGate = new();
    private bool _detectComplete;
    private bool _installRequested;
    // _isUpgrade is true when we found ANY trace of a prior install — either a
    // related MSI sharing UpgradeCode (DetectRelatedMsiPackage) or our chained
    // MSI itself reporting State=Present (DetectPackageComplete). Either way
    // the UI text flips from "Install" to "Update".
    private bool _isUpgrade;

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        _command = args.Command;
    }

    protected override void Run()
    {
        // Burn calls our Run() on the BA's main thread, which mbanative has
        // initialised as MTA (it can't be STA — see Program.Main comment).
        // WPF requires STA, so spawn a dedicated UI thread for the dispatcher
        // loop (only when we're going to actually show UI). Run() blocks on
        // _uiThread.Join() until the user closes the window, then returns to
        // mbanative which finalises the engine.
        //
        // Display modes:
        //   Full      - normal interactive run; show our 5-screen UI.
        //   Passive   - run automatically with progress only; we show our UI
        //               but auto-advance.
        //   None      - silent. No UI. Just drive Detect → Plan → Apply.
        //   Embedded  - parent is another bundle (Burn protocol); silent.
        //
        // Without this gate, Burn re-invoking us for related-bundle self-
        // uninstall during an upgrade pops a second installer window. That's
        // the "two windows" symptom on re-install.
        var display = _command?.Display ?? Display.Full;
        engine.Log(LogLevel.Standard, $"BA Run() display={display}");
        var showUi = display == Display.Full || display == Display.Passive;
        if (showUi)
        {
            StartUiThread();
            _uiReady.Wait();
        }

        DetectComplete += OnDetectComplete;
        // Existing-install detection: when Burn finds a related MSI by
        // UpgradeCode (different ProductCode, e.g. an older version on
        // disk), flag this as an upgrade.
        DetectRelatedMsiPackage += (_, e) =>
        {
            _isUpgrade = true;
            engine.Log(LogLevel.Standard, $"Related MSI detected: ProductCode={e.ProductCode}, version={e.Version}");
        };
        // Same-version detection: the chained MSI itself reports State=Present
        // when re-running the bundle on a machine that already has it. Without
        // this, re-running the installer reads "Install" rather than "Update"
        // and the user has no idea we noticed.
        DetectPackageComplete += (_, e) =>
        {
            if (e.State == PackageState.Present)
            {
                _isUpgrade = true;
                engine.Log(LogLevel.Standard, $"Package {e.PackageId} already present.");
            }
        };
        PlanComplete += OnPlanComplete;
        ApplyComplete += OnApplyComplete;
        Error += OnError;
        ExecuteProgress += OnExecuteProgress;
        CacheAcquireProgress += OnCacheAcquireProgress;
        // Granular status: surface what the engine is doing under the bar.
        CacheBegin += (_, _) => UpdateUi(vm => vm.Progress.Status = _isUpgrade ? "Preparing update…" : "Preparing files…");
        CachePackageBegin += (_, _) => UpdateUi(vm => vm.Progress.CurrentOperation = "Extracting payload…");
        CacheComplete += (_, _) => UpdateUi(vm => vm.Progress.CurrentOperation = string.Empty);
        ExecuteBegin += (_, _) => UpdateUi(vm =>
        {
            vm.Progress.Status = _isUpgrade ? "Updating…" : "Installing…";
            vm.Progress.CurrentOperation = "Starting Windows Installer…";
        });
        ExecutePackageBegin += (_, _) => UpdateUi(vm =>
            vm.Progress.CurrentOperation = _isUpgrade ? "Updating Startup Groups…" : "Installing Startup Groups…");
        ExecuteMsiMessage += OnExecuteMsiMessage;
        // Files-in-use: the running app holds locks on Program Files\Startup
        // Groups\StartupGroups.exe. Tell MSI to ignore — it'll queue the
        // replace for next reboot rather than stalling waiting for input.
        // Phase 3.1 should kill the running instance instead.
        ExecuteFilesInUse += (_, e) =>
        {
            engine.Log(LogLevel.Standard, $"FilesInUse for {e.Files?.Count ?? 0} files; replying Ignore");
            e.Result = Result.Ignore;
        };

        var action = _command?.Action ?? LaunchAction.Install;
        engine.Log(LogLevel.Standard, $"Phase 3c BA: Run() entered, action={action}, display={display}");

        // Action-specific routing:
        //   Install      → Welcome → License → Progress (the default path).
        //   Uninstall    → UninstallOptions screen first (lets the user opt
        //                  into wiping user-data folders), then Progress.
        //   Repair/Modify→ Skip Welcome, go straight to Progress.
        //   non-UI       → Drive Detect→Plan→Apply silently.
        if (action == LaunchAction.Uninstall && showUi)
        {
            UpdateUi(vm =>
            {
                vm.Progress.Status = "Uninstalling…";
                vm.Show(InstallerStep.UninstallOptions);
            });
            // Don't set _installRequested yet — we wait for the user to click
            // the Uninstall button on the options screen (OnUninstallConfirmed).
        }
        else if (action != LaunchAction.Install || !showUi)
        {
            if (showUi)
            {
                UpdateUi(vm =>
                {
                    vm.Progress.Status = action switch
                    {
                        LaunchAction.Repair => "Repairing…",
                        LaunchAction.Modify => "Updating…",
                        _ => "Working…",
                    };
                    vm.Show(InstallerStep.Progress);
                });
            }
            // Mark "user has consented" so OnDetectComplete drives Plan
            // automatically without waiting for an Install button click.
            lock (_stateGate) { _installRequested = true; }
        }

        // Detect runs in the background while the user reads Welcome/License.
        // It's fast (typically <100ms), so by the time they click Install
        // on the License screen, we already know the install state.
        engine.Detect();

        if (showUi)
        {
            // Block until the WPF dispatcher loop exits (window closed).
            _uiThread?.Join();
        }
        else
        {
            // Silent / Embedded: wait for ApplyComplete before quitting.
            _silentDone.Wait();
        }

        engine.Quit(0);
    }

    private readonly ManualResetEventSlim _silentDone = new();

    private void StartUiThread()
    {
        _uiThread = new Thread(() =>
        {
            // Apartment is set on the thread before Start; verified STA here
            // for WPF. Application.Run drives the dispatcher loop until
            // window-Closed marshals an InvokeShutdown.
            _wpfApp = new App();
            _wpfApp.InitializeComponent();
            // OnStartup doesn't fire when we drive the dispatcher manually;
            // apply the theme explicitly. Mica chrome itself comes from
            // FluentWindow's WindowBackdropType="Mica".
            ApplicationThemeManager.ApplySystemTheme(updateAccent: true);

            _viewModel = new InstallerWindowViewModel();
            _window = new InstallerWindow(_viewModel);
            _uiDispatcher = _window.Dispatcher;

            _viewModel.InstallRequested += OnInstallRequested;
            _viewModel.UninstallConfirmed += OnUninstallConfirmed;
            _viewModel.CloseRequested += OnCloseRequested;
            _viewModel.LaunchAppRequested += OnLaunchAppRequested;
            _window.Closed += (_, _) => _uiDispatcher.InvokeShutdown();

            _window.Show();
            _uiReady.Set();
            Dispatcher.Run();
        })
        {
            Name = "InstallerUiThread",
            IsBackground = false,
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
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

    private void OnUninstallConfirmed(object? sender, EventArgs e)
    {
        // Same shape as OnInstallRequested but for the Uninstall flow: the
        // user just clicked Uninstall on the options screen, gating Plan on
        // their explicit confirmation.
        bool startNow;
        lock (_stateGate)
        {
            _installRequested = true;
            startNow = _detectComplete;
        }
        if (startNow) BeginPlan();
    }

    private void OnLaunchAppRequested(object? sender, EventArgs e)
    {
        var appPath = ResolveInstalledAppPath();
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
        {
            engine.Log(LogLevel.Error, $"Launch requested but app not found at {appPath}");
            return;
        }

        // Always pass --show-main-window so the user actually sees the app
        // after clicking Launch, regardless of any pre-existing settings.json
        // that may have ShowMainWindowOnLaunch=false carried forward from an
        // earlier install. Forward the auto-start choice too — App.OnStartup
        // picks it up to register the Task Scheduler entry on first launch.
        var enableAutoStart = _viewModel?.Customize.EnableAutoStart == true;
        var args = enableAutoStart
            ? "--show-main-window --enable-autostart"
            : "--show-main-window";

        // UseShellExecute=false gives us a deterministic CreateProcess. The BA
        // runs unelevated; child inherits that. WorkingDirectory ensures the
        // app's relative paths (Assets\, etc.) resolve.
        var psi = new ProcessStartInfo
        {
            FileName = appPath,
            Arguments = args,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? string.Empty,
        };

        try
        {
            using var proc = Process.Start(psi);
            engine.Log(LogLevel.Standard, $"Launched {appPath} {args} (PID {proc?.Id})");
        }
        catch (Exception ex)
        {
            engine.Log(LogLevel.Error, $"Failed to launch {appPath}: {ex}");
        }
    }

    private static string? ResolveInstalledAppPath()
    {
        // The MSI uses Scope="perUserOrMachine" so INSTALLFOLDER lands in one
        // of three places depending on elevation: per-user under
        // %LocalAppData%\Programs (no UAC) or per-machine under either
        // ProgramFiles64 / ProgramFilesX86. Check per-user first because that
        // is what we get on an unelevated bundle run, which is the default.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Startup Groups", "StartupGroups.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Startup Groups", "StartupGroups.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Startup Groups", "StartupGroups.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
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

        // Bundle-level detection: WixBundleInstalled is set to 1 by Burn when
        // the bundle's own ARP key is present. This is the canonical "we are
        // already on this machine" signal — independent of chained-MSI
        // ProductCode/version churn between dev builds (which can otherwise
        // leave DetectPackageComplete reporting State=Absent).
        try
        {
            if (engine.GetVariableNumeric("WixBundleInstalled") == 1)
            {
                _isUpgrade = true;
                engine.Log(LogLevel.Standard, "WixBundleInstalled=1 → flagging as upgrade.");
            }
        }
        catch (Exception ex)
        {
            engine.Log(LogLevel.Verbose, $"WixBundleInstalled lookup failed: {ex.Message}");
        }

        // If anything flagged us as an upgrade (related MSI, package present,
        // bundle already installed), retitle the buttons + screens so the UX
        // reflects "Update" instead of "Install".
        if (_isUpgrade)
        {
            UpdateUi(vm =>
            {
                vm.Welcome.IsUpgrade = true;
                vm.Success.IsUpgrade = true;
            });
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

        // Kill any running StartupGroups.exe so MSI can actually replace the
        // binaries instead of queuing the replace for next reboot. Without
        // this, FilesInUse=Ignore (our handler) means new files never land —
        // Launch and Start-menu searches keep finding the old binaries.
        StopRunningInstances();

        UpdateUi(vm => vm.Progress.Status = "Installing…");

        // Burn needs a non-null hwnd so it can parent UAC and Windows
        // Installer prompts to our window. WindowInteropHelper.Handle has to
        // be read on the UI thread (it touches the WPF window).
        var hwnd = IntPtr.Zero;
        if (_uiDispatcher is not null && _window is not null)
        {
            _uiDispatcher.Invoke(() =>
            {
                hwnd = new WindowInteropHelper(_window).EnsureHandle();
            });
        }
        engine.Apply(hwnd);
    }

    private void StopRunningInstances()
    {
        // GetProcessesByName takes the image name without ".exe". Matches by
        // process executable, not module — so this finds the user's running
        // StartupGroups.exe regardless of where it was launched from.
        var processes = Process.GetProcessesByName("StartupGroups");
        if (processes.Length == 0) return;

        UpdateUi(vm => vm.Progress.CurrentOperation = "Closing existing instance…");
        var ourPid = Environment.ProcessId;

        foreach (var p in processes)
        {
            try
            {
                if (p.Id == ourPid) continue; // belt-and-braces; can't match anyway

                engine.Log(LogLevel.Standard, $"Closing running StartupGroups pid={p.Id}");
                // Try graceful close (sends WM_CLOSE to the main window).
                // If the app is in tray with no visible main window,
                // CloseMainWindow returns false — fall straight to Kill.
                if (!p.CloseMainWindow())
                {
                    p.Kill();
                }
                if (!p.WaitForExit(3000))
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                engine.Log(LogLevel.Error, $"Failed to close pid={p.Id}: {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        engine.Log(LogLevel.Standard, $"ApplyComplete status=0x{e.Status:X8}");

        var action = _command?.Action ?? LaunchAction.Install;

        if (e.Status >= 0 && action != LaunchAction.Uninstall)
        {
            // Write the user's chosen update channel into the per-user
            // settings.json before the app first launches. We only write if
            // the file doesn't already exist — preserves any prior config
            // (re-install / upgrade). Skipped on uninstall: there's no app
            // left to read it.
            TrySeedFirstRunSettings();
        }
        else if (e.Status >= 0 && action == LaunchAction.Uninstall)
        {
            // Honour the checkboxes from the UninstallOptions screen. MSI only
            // owns Program Files / per-user install dir; user-data folders are
            // ours to clean up if the user opts in.
            TryDeleteUserDataFolders();
        }

        UpdateUi(vm =>
        {
            if (e.Status >= 0)
            {
                // Final-screen UX depends on what we just did. Uninstall has no
                // app left to launch, so we hide the Launch button and retitle
                // accordingly. Install / Update / Repair / Modify all keep the
                // launch CTA — there's a working app on disk.
                vm.Success.IsUninstall = action == LaunchAction.Uninstall;
                vm.Success.IsUpgrade = _isUpgrade && action != LaunchAction.Uninstall;
                vm.Progress.Status = action == LaunchAction.Uninstall ? "Uninstalled." : "Done.";
                vm.Progress.Progress = 1.0;
                vm.Show(InstallerStep.Success);
            }
            else
            {
                vm.Progress.HasFailed = true;
                vm.Progress.FailureMessage = action == LaunchAction.Uninstall
                    ? $"Uninstall failed (0x{e.Status:X8}). Check the log for details."
                    : $"Install failed (0x{e.Status:X8}). Check the log for details.";
                vm.Progress.Status = "Failed.";
            }
        });

        // Silent / Embedded path: no UI to wait on. Release Run() so the BA
        // can engine.Quit() and let Burn finalise.
        _silentDone.Set();
    }

    private void TryDeleteUserDataFolders()
    {
        var deleteConfig = _viewModel?.UninstallOptions.DeleteUserConfig == true;
        var deleteLogs = _viewModel?.UninstallOptions.DeleteLogsAndCache == true;
        if (!deleteConfig && !deleteLogs) return;

        if (deleteConfig)
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StartupGroups");
            TryDeleteFolder(roaming, "user config");
        }
        if (deleteLogs)
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StartupGroups");
            TryDeleteFolder(local, "logs/cache");
        }
    }

    private void TryDeleteFolder(string path, string label)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                engine.Log(LogLevel.Standard, $"Deleted {label} folder: {path}");
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup. A locked log file (Serilog from a still-shutting-
            // down app process) shouldn't fail the uninstall.
            engine.Log(LogLevel.Error, $"Failed to delete {label} at {path}: {ex.Message}");
        }
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

    private void OnExecuteMsiMessage(object? sender, ExecuteMsiMessageEventArgs e)
    {
        // Windows Installer fires a lot of message types; we only want the
        // human-readable per-action descriptions ("Copying new files",
        // "Updating component registration", etc.). MSI also fires
        // ActionStart for component-level actions whose payload is the bare
        // Component GUID — visually meaningless to a user. Filter those out.
        if (e.MessageType != InstallMessage.ActionStart) return;
        var message = e.Message;
        if (string.IsNullOrWhiteSpace(message)) return;
        if (LooksLikeRawGuid(message)) return;

        UpdateUi(vm => vm.Progress.CurrentOperation = message);
    }

    private static bool LooksLikeRawGuid(string s)
    {
        var trimmed = s.Trim();
        // {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX} or with no braces. 36 or 38 chars.
        if (trimmed.Length is not (36 or 38)) return false;
        return Guid.TryParse(trimmed, out _);
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
