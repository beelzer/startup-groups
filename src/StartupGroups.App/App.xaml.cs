using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using StartupGroups.App.Localization;
using StartupGroups.App.Resources;
using StartupGroups.App.Services;
using StartupGroups.App.ViewModels;
using StartupGroups.App.Views;
using StartupGroups.Core.Branding;
using StartupGroups.Core.Elevation;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Launch.Probes;
using StartupGroups.Core.Services;
using StartupGroups.Core.WindowsStartup;
using Wpf.Ui.Appearance;

namespace StartupGroups.App;

public partial class App : Application
{
    private IHost? _host;
    private TrayViewModel? _trayViewModel;

    public static IServiceProvider Services =>
        ((App)Current)._host!.Services;

    private const string SkipElevateFlag = "--no-elevate-relaunch";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pin the AUMID before any window is created so Windows associates the
        // running process with the pinned shortcut's identity. Otherwise the
        // taskbar treats them as separate apps and silently uses a stale icon.
        TrySetAppUserModelId();

        AppPaths.EnsureUserDirectories();

        if (TryRelaunchAsAdminIfConfigured(e.Args))
        {
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogFolder, AppIdentifiers.LogFileRollingPattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled AppDomain exception");
                Log.CloseAndFlush();
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) => ConfigureServices(services))
            .Build();

        _host.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Apply persisted UI culture BEFORE any window/binding is constructed.
        _host.Services.GetRequiredService<ILanguageService>().ApplyPersisted();

        ApplyConfiguredTheme();

        var settings = _host.Services.GetRequiredService<ISettingsStore>();
        settings.Changed += (_, _) => Dispatcher.Invoke(ApplyConfiguredTheme);

        var configStore = _host.Services.GetRequiredService<IConfigStore>();
        configStore.Load();
        configStore.BeginWatching();

        _trayViewModel = _host.Services.GetRequiredService<TrayViewModel>();
        _trayViewModel.Initialize();

        // If launched by Velopack after applying an update, surface the main
        // window so the user lands back in the app showing the new version
        // instead of just a tray icon.
        if (e.Args.Any(a => string.Equals(a, VelopackUpdateService.RestartedAfterUpdateArg, StringComparison.OrdinalIgnoreCase)))
        {
            _trayViewModel.ShowMainWindowCommand.Execute(null);
        }
    }

    private void ApplyConfiguredTheme()
    {
        var settings = _host!.Services.GetRequiredService<ISettingsStore>();
        switch (settings.Current.Theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, updateAccent: true);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, updateAccent: true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayViewModel?.Dispose();
        if (_host is not null)
        {
            _host.StopAsync(Timeouts.HostShutdownGrace).GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPathResolver, PathResolver>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IProcessInspector, ProcessInspector>();
        services.AddSingleton<IProcessMatcherResolver, ProcessMatcherResolver>();
        services.AddSingleton<IKnownAppsDatabase, KnownAppsDatabase>();
        services.AddSingleton<IServiceController, WindowsServiceController>();

        services.AddSingleton<IReadinessProbe, MainWindowProbe>();
        services.AddSingleton<IReadinessProbe, WaitForInputIdleProbe>();
        services.AddSingleton<IReadinessProbe, ActivityQuietProbe>();
        services.AddSingleton<IReadinessProbe, ServiceRunningProbe>();
        services.AddSingleton<ReadinessDetector>();
        services.AddSingleton<ILaunchBenchmarkStore>(sp =>
        {
            var store = new SqliteLaunchBenchmarkStore(
                databasePath: null,
                logger: sp.GetRequiredService<ILogger<SqliteLaunchBenchmarkStore>>());
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });
        services.AddSingleton<EtwResourceMonitor>();
        services.AddSingleton<DependencyHintsAnalyzer>();
        services.AddSingleton<ILaunchTelemetryService, LaunchTelemetryService>();

        services.AddSingleton<IAppOrchestrator, AppOrchestrator>();

        services.AddSingleton<IConfigStore>(sp =>
            new JsonConfigStore(AppPaths.ConfigFilePath, sp.GetRequiredService<ILogger<JsonConfigStore>>()));

        services.AddSingleton<IElevationClient>(sp =>
            new ElevationClient(ElevationPaths.ResolveElevatorPath(), sp.GetRequiredService<ILogger<ElevationClient>>()));

        services.AddSingleton<IAutoStartService, TaskSchedulerAutoStartService>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ILanguageService, LanguageService>();
        services.AddSingleton<IUpdateService, VelopackUpdateService>();
        services.AddSingleton<IWindowsStartupService, WindowsStartupService>();
        services.AddSingleton<ShellInstalledAppsProvider>();
        services.AddSingleton<WindowsServicesProvider>();
        services.AddSingleton<ScoopInstalledAppsProvider>();
        services.AddSingleton<IInstalledAppsProvider>(sp => new CompositeInstalledAppsProvider(
        [
            sp.GetRequiredService<ShellInstalledAppsProvider>(),
            sp.GetRequiredService<WindowsServicesProvider>(),
            sp.GetRequiredService<ScoopInstalledAppsProvider>()
        ]));

        services.AddSingleton<WindowsStartupViewModel>();
        services.AddSingleton<BenchmarksViewModel>();
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<GroupEditorViewModel>();
        services.AddTransient<AppEntryEditorViewModel>();
        services.AddTransient<AddAppPickerViewModel>();
        services.AddTransient<RegistryRunValueEditorViewModel>();
        services.AddTransient<UpdateFlyoutViewModel>();

        services.AddSingleton<MainWindow>();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);

    private static void TrySetAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppIdentifiers.AppUserModelId);
        }
        catch (Exception ex)
        {
            // Non-fatal — we just lose the icon-grouping benefit.
            Log.Warning(ex, "Failed to set AppUserModelID");
        }
    }

    private bool TryRelaunchAsAdminIfConfigured(string[] args)
    {
        if (args.Any(a => string.Equals(a, SkipElevateFlag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            if (StartupGroups.Core.Launch.ElevationDetector.IsElevated)
            {
                return false;
            }

            var settings = new SettingsStore().Current;
            if (!settings.AlwaysRunAsAdmin)
            {
                return false;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            var forwarded = string.Join(' ', args.Concat(new[] { SkipElevateFlag }));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = forwarded,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            };
            System.Diagnostics.Process.Start(psi);
            Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User denied UAC; fall through and run non-elevated.
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Relaunch-as-admin failed");
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception");
        MessageBox.Show(
            e.Exception.Message,
            string.Format(CultureInfo.CurrentUICulture, Strings.Dialog_UnexpectedErrorFormat, AppBranding.AppName),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
