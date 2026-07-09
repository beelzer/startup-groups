using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Salvo.App.Localization;
using Salvo.App.Resources;
using Salvo.App.Services;
using Salvo.App.Views;
using Salvo.Core.Branding;
using Salvo.Core.Elevation;
using Salvo.Core.Launch;
using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.App.ViewModels;

public enum ActiveView
{
    Groups,
    WindowsStartup,
    Benchmarks,
    Settings,
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IConfigStore _configStore;
    private readonly IAppOrchestrator _orchestrator;
    private readonly IElevationClient _elevation;
    private readonly IAutoStartService _autoStart;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly ILanguageService _languageService;
    private readonly IUpdateService _updateService;
    private readonly ILaunchTelemetryService? _telemetry;
    private readonly ILaunchBenchmarkStore? _benchmarkStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private bool _suppressSettingsSave;
    private bool _updateCheckScheduled;

    public MainWindowViewModel(
        IConfigStore configStore,
        IAppOrchestrator orchestrator,
        IElevationClient elevation,
        IAutoStartService autoStart,
        ISettingsStore settings,
        IDialogService dialogs,
        ILanguageService languageService,
        IUpdateService updateService,
        WindowsStartupViewModel windowsStartup,
        IServiceProvider serviceProvider,
        ILogger<MainWindowViewModel> logger,
        ILaunchTelemetryService? telemetry = null,
        ILaunchBenchmarkStore? benchmarkStore = null,
        BenchmarksViewModel? benchmarks = null)
    {
        _configStore = configStore;
        _orchestrator = orchestrator;
        _elevation = elevation;
        _autoStart = autoStart;
        _settings = settings;
        _dialogs = dialogs;
        _languageService = languageService;
        _updateService = updateService;
        _telemetry = telemetry;
        _benchmarkStore = benchmarkStore;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        WindowsStartup = windowsStartup;
        Benchmarks = benchmarks;

        if (_telemetry is not null)
        {
            _telemetry.MetricsSaved += OnMetricsSaved;
        }

        ThemeOptions =
        [
            new ThemeOption(AppTheme.System),
            new ThemeOption(AppTheme.Light),
            new ThemeOption(AppTheme.Dark),
        ];
        AvailableLanguages = SupportedLanguages.All;

        // Initial load from persisted settings (suppress save loop)
        _suppressSettingsSave = true;
        var current = _settings.Current;
        _selectedTheme = ThemeOptions.First(o => o.Value == current.Theme);
        _selectedLanguage = _languageService.Current;
        _minimizeToTrayOnClose = current.MinimizeToTrayOnClose;
        _showNotifications = current.ShowNotifications;
        _showMainWindowOnLaunch = current.ShowMainWindowOnLaunch;
        _appsViewMode = current.AppsViewMode;
        _autoStartEnabled = _autoStart.IsEnabled();
        _alwaysRunAsAdmin = current.AlwaysRunAsAdmin;
        _warnWhenElevatedAppsPresent = current.WarnWhenElevatedAppsPresent;
        _isRunningAsAdmin = ElevationDetector.IsElevated;
        _suppressSettingsSave = false;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = Timeouts.StatusRefreshInterval
        };
        _statusTimer.Tick += (_, _) => RefreshRunningStates();

        _configStore.Changed += OnConfigStoreChanged;

        Groups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAnyGroupContent));
            OnPropertyChanged(nameof(ShouldShowElevationWarning));
        };

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;

        LoadFromConfig(_configStore.Load());

        // First-run / empty-config fallback: a blank Groups page is a dead
        // end. Land on Windows Startup instead so the user immediately sees
        // what their machine is already auto-launching.
        if (Groups.Count == 0)
        {
            _suppressHistoryRecording = true;
            try { ActiveView = ActiveView.WindowsStartup; }
            finally { _suppressHistoryRecording = false; }
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        OnPropertyChanged(nameof(AdminStatusText));
        OnPropertyChanged(nameof(AppVersionLabel));
        OnPropertyChanged(nameof(LastCheckedText));
    }

    public ObservableCollection<GroupViewModel> Groups { get; } = [];
    public WindowsStartupViewModel WindowsStartup { get; }
    public IReadOnlyList<SupportedLanguage> AvailableLanguages { get; }
    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public string AppName => AppBranding.AppName;
    // Pull the running version from Velopack so canary builds display
    // "0.2.14-canary.6" rather than just the AppBranding.Version constant
    // ("0.2.14"). Falls back to AppBranding.Version in dev / non-Velopack
    // installs (see VelopackUpdateService.CurrentVersion).
    public string AppVersionLabel =>
        string.Format(CultureInfo.CurrentUICulture, Strings.Settings_About_VersionFormat, _updateService.CurrentVersion);
    public string CurrentVersionShort => $"v{_updateService.CurrentVersion}";
    public string LatestVersionShort => string.IsNullOrEmpty(LatestVersion) ? "" : $"v{LatestVersion}";
    public string AppSupportUrl => AppBranding.SupportUrl;
    public string LastCheckedText =>
        LastChecked is null
            ? string.Empty
            : string.Format(CultureInfo.CurrentUICulture, Strings.Settings_Version_LastCheckedFormat,
                LastChecked.Value.LocalDateTime);

    [ObservableProperty] private GroupViewModel? _selectedGroup;
    [ObservableProperty] private AppEntryViewModel? _selectedApp;
    [ObservableProperty] private bool _autoStartEnabled;
    [ObservableProperty] private ActiveView _activeView = ActiveView.Groups;
    [ObservableProperty] private SupportedLanguage _selectedLanguage;
    [ObservableProperty] private ThemeOption _selectedTheme;
    [ObservableProperty] private bool _minimizeToTrayOnClose;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private bool _showMainWindowOnLaunch;
    [ObservableProperty] private AppsViewMode _appsViewMode;

    [ObservableProperty] private bool _alwaysRunAsAdmin;
    [ObservableProperty] private bool _warnWhenElevatedAppsPresent;
    [ObservableProperty] private bool _isRunningAsAdmin;
    [ObservableProperty] private bool _isAdminCardExpanded;
    [ObservableProperty] private bool _isElevationWarningExpanded;

    // Session-only: clicking the X on the banner hides it for the current session only.
    // Survives view changes; reset when the app restarts.
    [ObservableProperty] private bool _isElevationWarningDismissedInSession;

    partial void OnIsElevationWarningDismissedInSessionChanged(bool value) =>
        OnPropertyChanged(nameof(ShouldShowElevationWarning));

    public bool CanToggleAdminSettings => IsRunningAsAdmin;
    public bool CanRestartAsAdmin => !IsRunningAsAdmin;
    public string AdminStatusText => IsRunningAsAdmin ? Strings.Admin_Status_Running : Strings.Admin_Status_Standard;

    // Show on any page that exposes admin-relevant controls: Groups (with content) or Windows Startup.
    // Session-dismissed banners stay hidden until the app restarts.
    public bool ShouldShowElevationWarning =>
        WarnWhenElevatedAppsPresent
        && !IsRunningAsAdmin
        && !IsElevationWarningDismissedInSession
        && (IsStartupView || (IsGroupsView && HasAnyGroupContent));

    // Small title-bar pill indicator; gated on the same user preference as the legacy banner.
    public bool ShouldShowAdminPill => WarnWhenElevatedAppsPresent;

    public bool HasAnyGroupContent => Groups.Any(g => g.Apps.Count > 0);

    partial void OnIsRunningAsAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleAdminSettings));
        OnPropertyChanged(nameof(CanRestartAsAdmin));
        OnPropertyChanged(nameof(AdminStatusText));
        OnPropertyChanged(nameof(ShouldShowElevationWarning));
    }

    partial void OnWarnWhenElevatedAppsPresentChanged(bool value)
    {
        if (!_suppressSettingsSave)
        {
            PersistSettings(s => s.WarnWhenElevatedAppsPresent = value);
        }
        OnPropertyChanged(nameof(ShouldShowElevationWarning));
        OnPropertyChanged(nameof(ShouldShowAdminPill));
    }

    [RelayCommand]
    private void DismissElevationWarning()
    {
        // Session-only dismissal: the persisted setting stays on so the banner returns next launch.
        // To turn it off permanently, use the toggle on the Settings page.
        IsElevationWarningDismissedInSession = true;
    }

    public event EventHandler? AdminCardHighlightRequested;

    [RelayCommand]
    private void NavigateToAdminSettings()
    {
        ActiveView = ActiveView.Settings;
        IsAdminCardExpanded = true;
        // The View subscribes and handles BringIntoView + flash effect.
        AdminCardHighlightRequested?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _latestVersion = string.Empty;
    [ObservableProperty] private string? _releaseUrl;
    [ObservableProperty] private DateTimeOffset? _lastChecked;
    [ObservableProperty] private bool _isCheckingForUpdate;
    private UpdateCheckResult? _lastCheckResult;

    partial void OnIsUpdateAvailableChanged(bool value) =>
        ShowUpdateDetailsCommand.NotifyCanExecuteChanged();

    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(LatestVersionShort));
    partial void OnLastCheckedChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastCheckedText));

    public bool IsGroupsView => ActiveView == ActiveView.Groups;
    public bool IsStartupView => ActiveView == ActiveView.WindowsStartup;
    public bool IsBenchmarksView => ActiveView == ActiveView.Benchmarks;
    public bool IsSettingsView => ActiveView == ActiveView.Settings;

    public BenchmarksViewModel? Benchmarks { get; set; }

    // ===== Back/forward navigation history =====
    private readonly Stack<NavigationState> _backStack = new();
    private readonly Stack<NavigationState> _forwardStack = new();
    private bool _suppressHistoryRecording;

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    partial void OnActiveViewChanging(ActiveView oldValue, ActiveView newValue)
    {
        if (oldValue == newValue || _suppressHistoryRecording) return;
        _backStack.Push(new NavigationState(oldValue, SelectedGroup));
        _forwardStack.Clear();
        NotifyHistoryChanged();
    }

    partial void OnSelectedGroupChanging(GroupViewModel? oldValue, GroupViewModel? newValue)
    {
        if (ReferenceEquals(oldValue, newValue) || _suppressHistoryRecording) return;
        // Only record group-to-group changes inside Groups view. When the user switches away
        // from Groups via a sidebar command, the command sets ActiveView FIRST (which records
        // the navigation itself), THEN suppresses and nulls SelectedGroup — so this hook is
        // safely bypassed for the synthetic clear.
        if (ActiveView != ActiveView.Groups) return;
        _backStack.Push(new NavigationState(ActiveView.Groups, oldValue));
        _forwardStack.Clear();
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        var current = new NavigationState(ActiveView, SelectedGroup);
        var target = _backStack.Pop();
        ApplyStateSuppressed(target);
        _forwardStack.Push(current);
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        var current = new NavigationState(ActiveView, SelectedGroup);
        var target = _forwardStack.Pop();
        ApplyStateSuppressed(target);
        _backStack.Push(current);
        NotifyHistoryChanged();
    }

    private void ApplyStateSuppressed(NavigationState state)
    {
        _suppressHistoryRecording = true;
        try
        {
            SelectedGroup = state.Group;
            ActiveView = state.View;
        }
        finally
        {
            _suppressHistoryRecording = false;
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private void ClearSelectedGroupSuppressed()
    {
        if (SelectedGroup is null) return;
        _suppressHistoryRecording = true;
        try { SelectedGroup = null; }
        finally { _suppressHistoryRecording = false; }
    }

    private sealed record NavigationState(ActiveView View, GroupViewModel? Group);
    // ===== /history =====

    // True only when the Groups view is active AND a group is selected; drives the title-bar
    // contextual header (group name + launch/stop/rename/delete buttons).
    public bool IsGroupsViewWithSelection => IsGroupsView && SelectedGroup is not null;

    partial void OnActiveViewChanged(ActiveView value)
    {
        OnPropertyChanged(nameof(IsGroupsView));
        OnPropertyChanged(nameof(IsStartupView));
        OnPropertyChanged(nameof(IsBenchmarksView));
        OnPropertyChanged(nameof(IsSettingsView));
        OnPropertyChanged(nameof(IsGroupsViewWithSelection));
        OnPropertyChanged(nameof(ShouldShowElevationWarning));

        if (value == ActiveView.WindowsStartup)
        {
            WindowsStartup.Refresh();
        }
        if (value == ActiveView.Benchmarks && Benchmarks is not null)
        {
            _ = Benchmarks.RefreshAsync();
        }
        if (value == ActiveView.Settings && !_updateCheckScheduled)
        {
            _updateCheckScheduled = true;
            // Auto-check on first Settings open uses the disk cache (force=false).
            // Manual "Check now" passes force=true.
            _ = CheckForUpdatesAsync(force: false);
        }
    }

    partial void OnSelectedGroupChanged(GroupViewModel? value)
    {
        if (value is not null)
        {
            ActiveView = ActiveView.Groups;
        }
        OnPropertyChanged(nameof(IsGroupsViewWithSelection));
    }

    partial void OnSelectedLanguageChanged(SupportedLanguage value)
    {
        if (_suppressSettingsSave || value is null)
        {
            return;
        }
        _languageService.SetLanguage(value);
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (_suppressSettingsSave || value is null)
        {
            return;
        }
        PersistSettings(s => s.Theme = value.Value);
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (_suppressSettingsSave) return;
        PersistSettings(s => s.MinimizeToTrayOnClose = value);
    }

    partial void OnShowNotificationsChanged(bool value)
    {
        if (_suppressSettingsSave) return;
        PersistSettings(s => s.ShowNotifications = value);
    }

    partial void OnShowMainWindowOnLaunchChanged(bool value)
    {
        if (_suppressSettingsSave) return;
        PersistSettings(s => s.ShowMainWindowOnLaunch = value);
    }

    partial void OnAppsViewModeChanged(AppsViewMode value)
    {
        if (_suppressSettingsSave) return;
        PersistSettings(s => s.AppsViewMode = value);
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        try
        {
            if (value)
            {
                _autoStart.Enable(AlwaysRunAsAdmin && IsRunningAsAdmin);
            }
            else
            {
                _autoStart.Disable();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle autostart");
            _ = _dialogs.ShowErrorAsync(Strings.Dialog_AutoStart_Title, ex.Message);
        }
    }

    partial void OnAlwaysRunAsAdminChanged(bool value)
    {
        if (_suppressSettingsSave) return;
        PersistSettings(s => s.AlwaysRunAsAdmin = value);

        try
        {
            if (_autoStart.IsEnabled())
            {
                _autoStart.Enable(value && IsRunningAsAdmin);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update autostart elevation");
            _ = _dialogs.ShowErrorAsync(Strings.Dialog_AutoStart_Title, ex.Message);
        }
    }


    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (IsRunningAsAdmin) return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            _ = _dialogs.ShowErrorAsync(Strings.Dialog_RestartAsAdmin_Title, Strings.Dialog_RestartAsAdmin_PathError);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };

        try
        {
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled the UAC prompt; stay where we are.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relaunch as administrator");
            _ = _dialogs.ShowErrorAsync(Strings.Dialog_RestartAsAdmin_Title, ex.Message);
        }
    }

    private void PersistSettings(Action<AppSettings> mutate)
    {
        var clone = _settings.Current.Clone();
        mutate(clone);
        _settings.Save(clone);
    }

    [RelayCommand]
    private void ShowGroupsView() => ActiveView = ActiveView.Groups;

    // Order matters: set ActiveView first so the navigation history snapshots {Groups, SelectedGroup}
    // correctly. Then suppress while nulling SelectedGroup so the null-out doesn't push a bogus
    // second entry.
    [RelayCommand]
    private void ShowStartupView()
    {
        ActiveView = ActiveView.WindowsStartup;
        ClearSelectedGroupSuppressed();
    }

    [RelayCommand]
    private void ShowBenchmarksView()
    {
        ActiveView = ActiveView.Benchmarks;
        ClearSelectedGroupSuppressed();
    }

    [RelayCommand]
    private void ShowSettingsView()
    {
        ActiveView = ActiveView.Settings;
        ClearSelectedGroupSuppressed();
    }

    [RelayCommand]
    private void OpenSupportUrl() => LaunchShell(AppBranding.SupportUrl, "support URL");

    [RelayCommand]
    private void OpenReleaseNotes() =>
        LaunchShell(ReleaseUrl ?? $"{AppBranding.SupportUrl}/releases/latest", "release notes");

    [RelayCommand(CanExecute = nameof(CanShowUpdateDetails))]
    private void ShowUpdateDetails()
    {
        // Not running under a Velopack install (dev / legacy MSI install) — fall
        // back to opening the release page so the user can grab Setup.exe manually.
        if (!_updateService.CanUpdate)
        {
            LaunchShell(ReleaseUrl ?? $"{AppBranding.SupportUrl}/releases/latest", "release page");
            return;
        }

        if (_lastCheckResult is null) return;

        var flyoutVm = _serviceProvider.GetRequiredService<UpdateFlyoutViewModel>();
        flyoutVm.Initialize(_lastCheckResult);
        var window = new UpdateFlyoutWindow(flyoutVm);
        window.ShowDialog();
    }

    private bool CanShowUpdateDetails() => IsUpdateAvailable;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => CheckForUpdatesAsync(force: true);

    private async Task CheckForUpdatesAsync(bool force)
    {
        IsCheckingForUpdate = true;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _updateService.CheckAsync(force).ConfigureAwait(true);
            if (result is null)
            {
                return;
            }

            _lastCheckResult = result;
            LatestVersion = result.LatestVersion ?? string.Empty;
            ReleaseUrl = result.ReleaseUrl;
            IsUpdateAvailable = result.IsUpdateAvailable;
            LastChecked = result.CheckedAt;
            ShowUpdateDetailsCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check threw unexpectedly.");
        }
        finally
        {
            IsCheckingForUpdate = false;
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdate;

    [RelayCommand]
    private void OpenWindowsColorSettings() => LaunchShell("ms-settings:colors", "Windows color settings");

    [RelayCommand]
    private void OpenTaskScheduler() => LaunchShell("taskschd.msc", "Task Scheduler");

    [RelayCommand]
    private void ReportTranslationIssue() => LaunchShell(AppBranding.IssueUrl, "translation issue page");

    private void LaunchShell(string target, string description)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open {Description}", description);
        }
    }

    public string ConfigPath => _configStore.ConfigPath;

    public void Start()
    {
        RefreshRunningStates();
        _statusTimer.Start();
    }

    public void Stop()
    {
        _statusTimer.Stop();
    }

    private void OnConfigStoreChanged(object? sender, Configuration config)
    {
        _dispatcher.Invoke(() => LoadFromConfig(config));
    }

    private void LoadFromConfig(Configuration config)
    {
        var previousId = SelectedGroup?.Id;
        Groups.Clear();
        foreach (var group in config.Groups)
        {
            Groups.Add(GroupViewModel.FromModel(group));
        }

        _suppressHistoryRecording = true;
        try
        {
            SelectedGroup = Groups.FirstOrDefault(g => g.Id == previousId) ?? Groups.FirstOrDefault();
        }
        finally
        {
            _suppressHistoryRecording = false;
        }
        RefreshRunningStates();
        AppIconLoader.LoadFor(Groups.SelectMany(g => g.Apps));
        _ = LoadHistoricalBenchmarksAsync();
    }

    private async Task LoadHistoricalBenchmarksAsync()
    {
        if (_benchmarkStore is null) return;

        var apps = Groups.SelectMany(g => g.Apps).ToList();
        foreach (var app in apps)
        {
            try
            {
                var recent = await _benchmarkStore.GetRecentAsync(app.ComputedAppId, 1).ConfigureAwait(true);
                if (recent.Count > 0)
                {
                    _dispatcher.Invoke(() => app.ApplyMetrics(recent[0]));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load history for {App}", app.Name);
            }
        }
    }

    private void OnMetricsSaved(object? sender, LaunchMetrics metrics)
    {
        _dispatcher.BeginInvoke(() =>
        {
            foreach (var group in Groups)
            {
                foreach (var app in group.Apps)
                {
                    if (string.Equals(app.ComputedAppId, metrics.AppId, StringComparison.Ordinal))
                    {
                        app.ApplyMetrics(metrics);
                        return;
                    }
                }
            }
        });
    }

    private int _refreshInFlight;

    private void RefreshRunningStates()
    {
        // Snapshot (app, model, current-state) on the UI thread, run the process-
        // table queries off it, then marshal the booleans back — the 3s tick must
        // not block the UI with O(apps x processes) IsRunning work. A re-entrancy
        // guard skips a tick while a prior sweep is still running.
        if (System.Threading.Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        var targets = Groups
            .SelectMany(g => g.Apps)
            .Select(app => (App: app, Model: app.ToModel(), Current: app.IsRunning))
            .ToList();

        if (targets.Count == 0)
        {
            System.Threading.Interlocked.Exchange(ref _refreshInFlight, 0);
            return;
        }

        _ = Task.Run(() =>
        {
            var states = new List<(AppEntryViewModel App, bool IsRunning)>(targets.Count);
            foreach (var (app, model, current) in targets)
            {
                bool running;
                try
                {
                    running = _orchestrator.IsRunning(model);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query running state for {App}", app.Name);
                    running = current;
                }
                states.Add((app, running));
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                foreach (var (app, running) in states) app.IsRunning = running;
                System.Threading.Interlocked.Exchange(ref _refreshInFlight, 0);
                return;
            }

            dispatcher.Invoke(() =>
            {
                foreach (var (app, running) in states)
                {
                    app.IsRunning = running;
                }
            });
            System.Threading.Interlocked.Exchange(ref _refreshInFlight, 0);
        });
    }

    public void PersistConfigPublic() => PersistConfig();

    private void PersistConfig()
    {
        var config = new Configuration
        {
            Groups = Groups.Select(g => g.ToModel()).ToList()
        };
        _configStore.Save(config);
    }

    [RelayCommand]
    private async Task LaunchGroupAsync(GroupViewModel? group)
    {
        if (group is null) return;

        var model = group.ToModel();
        var results = await _orchestrator.LaunchGroupAsync(model).ConfigureAwait(true);
        ApplyResults(group, results);
        await HandleElevationAsync(results, ElevationAction.Start).ConfigureAwait(true);
        RefreshRunningStates();
    }

    [RelayCommand]
    private async Task StopGroupAsync(GroupViewModel? group)
    {
        if (group is null) return;

        var model = group.ToModel();
        var results = await _orchestrator.StopGroupAsync(model).ConfigureAwait(true);
        ApplyResults(group, results);
        await HandleElevationAsync(results, ElevationAction.Stop).ConfigureAwait(true);
        RefreshRunningStates();
    }

    [RelayCommand]
    private async Task LaunchAppAsync(AppEntryViewModel? app)
    {
        if (app is null) return;

        var result = _orchestrator.LaunchApp(app.ToModel());
        app.LastStatus = result.Message;
        await MaybeElevateAsync(result, ElevationAction.Start).ConfigureAwait(true);
        RefreshRunningStates();
    }

    [RelayCommand]
    private async Task StopAppAsync(AppEntryViewModel? app)
    {
        if (app is null) return;

        var result = _orchestrator.StopApp(app.ToModel());
        app.LastStatus = result.Message;
        await MaybeElevateAsync(result, ElevationAction.Stop).ConfigureAwait(true);
        RefreshRunningStates();
    }

    [RelayCommand]
    private void AddGroup()
    {
        var editor = _serviceProvider.GetRequiredService<GroupEditorViewModel>();
        editor.IsNew = true;
        editor.Id = "";
        editor.Name = "";
        editor.Icon = AppBranding.DefaultGroupIcon;
        editor.LoadApps([]);

        var window = new GroupEditorWindow(editor);
        if (window.ShowDialog() != true)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(editor.Id))
        {
            editor.Id = Slugify(editor.Name);
        }

        if (Groups.Any(g => string.Equals(g.Id, editor.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _ = _dialogs.ShowErrorAsync(
                Strings.Dialog_DuplicateGroup_Title,
                string.Format(CultureInfo.CurrentUICulture, Strings.Dialog_DuplicateGroup_MessageFormat, editor.Id));
            return;
        }

        var group = new GroupViewModel
        {
            Id = editor.Id,
            Name = string.IsNullOrWhiteSpace(editor.Name) ? editor.Id : editor.Name,
            Icon = editor.Icon
        };
        Groups.Add(group);
        SelectedGroup = group;
        PersistConfig();
    }

    [RelayCommand]
    private void EditGroup(GroupViewModel? group)
    {
        if (group is null) return;

        var editor = _serviceProvider.GetRequiredService<GroupEditorViewModel>();
        editor.IsNew = false;
        editor.Id = group.Id;
        editor.Name = group.Name;
        editor.Icon = group.Icon;
        editor.LoadApps(group.Apps);

        var window = new GroupEditorWindow(editor);
        if (window.ShowDialog() != true)
        {
            return;
        }

        group.Id = editor.Id;
        group.Name = editor.Name;
        group.Icon = editor.Icon;
        PersistConfig();
    }

    [RelayCommand]
    private async Task RemoveGroupAsync(GroupViewModel? group)
    {
        if (group is null) return;

        if (!await _dialogs.ConfirmAsync(
            Strings.Dialog_RemoveGroup_Title,
            string.Format(CultureInfo.CurrentUICulture, Strings.Dialog_RemoveGroup_MessageFormat, group.Name)))
        {
            return;
        }

        Groups.Remove(group);
        SelectedGroup = Groups.FirstOrDefault();
        PersistConfig();
    }

    /// <summary>
    /// Add a new <see cref="Salvo.App.ViewModels.Flow.AppNodeViewModel"/> to the group's graph,
    /// running after every current leaf (node with no outgoing edges) —
    /// i.e. "appended to the end of the chain". The new node becomes the
    /// new sole leaf, so subsequent appends extend the chain linearly.
    /// </summary>
    private static Salvo.App.ViewModels.Flow.AppNodeViewModel AppendAppNode(GroupViewModel group, AppEntryViewModel app)
    {
        var leaves = group.Graph.Nodes
            .Where(n => !group.Graph.Edges.Any(e => e.From == n.Id))
            .ToList();
        // Fallback: empty graph (shouldn't happen — Start is always
        // present) — anchor on Start as the predecessor.
        if (leaves.Count == 0 && group.Graph.Start is not null)
        {
            leaves = [group.Graph.Start];
        }

        var newNode = new Salvo.App.ViewModels.Flow.AppNodeViewModel
        {
            Id = Guid.NewGuid().ToString(),
            App = app,
        };
        group.Graph.Nodes.Add(newNode);
        foreach (var leaf in leaves)
        {
            group.Graph.Edges.Add(new Salvo.App.ViewModels.Flow.EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = leaf.Id,
                To = newNode.Id,
            });
        }
        group.Graph.RebuildStages();
        return newNode;
    }

    [RelayCommand]
    private void EditApp(AppEntryViewModel? app)
    {
        if (app is null) return;

        var editor = _serviceProvider.GetRequiredService<AppEntryEditorViewModel>();
        editor.IsNew = false;
        editor.LoadFrom(app);

        var window = new AppEntryEditorWindow(editor);
        if (window.ShowDialog() != true) return;

        editor.ApplyTo(app);
        PersistConfig();
    }

    public void ReorderGroup(GroupViewModel source, int targetIndex)
    {
        var srcIdx = Groups.IndexOf(source);
        if (srcIdx < 0) return;

        var clamped = Math.Clamp(targetIndex, 0, Groups.Count - 1);
        if (clamped == srcIdx) return;

        Groups.Move(srcIdx, clamped);
        PersistConfig();
    }

    // ---- Flow editor commands -----------------------------------------

    /// <summary>
    /// Exposed for the GroupCallNode card's group picker so it can
    /// resolve target groups by id.
    /// </summary>
    public IReadOnlyList<GroupViewModel> AllGroups => Groups;

    /// <summary>
    /// Factory mapping a node-kind string (from the add menus) to a fresh
    /// node view-model. Shared by the top-level add, the per-stage insert,
    /// and the If-branch add so the three stay in lockstep. "App" pops the
    /// entry editor and returns null if the user cancels.
    /// </summary>
    private Salvo.App.ViewModels.Flow.NodeViewModel? CreateNode(string kind) => kind switch
    {
        "App" => BuildAppNodeViaEditor(),
        "Wait" => new Salvo.App.ViewModels.Flow.WaitNodeViewModel { Id = Guid.NewGuid().ToString(), DurationSeconds = 5 },
        "IfElse" => new Salvo.App.ViewModels.Flow.IfElseNodeViewModel { Id = Guid.NewGuid().ToString() },
        "ServiceStart" => new Salvo.App.ViewModels.Flow.ServiceStartNodeViewModel { Id = Guid.NewGuid().ToString() },
        "ServiceStop" => new Salvo.App.ViewModels.Flow.ServiceStopNodeViewModel { Id = Guid.NewGuid().ToString() },
        "RunCommand" => new Salvo.App.ViewModels.Flow.RunCommandNodeViewModel { Id = Guid.NewGuid().ToString() },
        "GroupCall" => new Salvo.App.ViewModels.Flow.GroupCallNodeViewModel { Id = Guid.NewGuid().ToString() },
        _ => null,
    };

    [RelayCommand]
    private void AddNode(string? kind)
    {
        if (SelectedGroup is null || string.IsNullOrEmpty(kind)) return;

        // "App" goes through the installed-app / service scanner, which can
        // return several apps at once; each is appended to the chain end.
        if (kind == "App")
        {
            var appNodes = PickAppNodes();
            foreach (var n in appNodes) AppendAppNode(SelectedGroup, n.App);
            if (appNodes.Count > 0)
            {
                AppIconLoader.LoadFor(appNodes.Select(n => n.App).ToList());
                RefreshRunningStates();
                PersistConfig();
            }
            return;
        }

        var newNode = CreateNode(kind);
        if (newNode is null) return;

        // Append after the current leaves of the graph.
        var leaves = SelectedGroup.Graph.Nodes
            .Where(n => !SelectedGroup.Graph.Edges.Any(e => e.From == n.Id))
            .ToList();
        if (leaves.Count == 0 && SelectedGroup.Graph.Start is not null)
        {
            leaves = [SelectedGroup.Graph.Start];
        }

        SelectedGroup.Graph.Nodes.Add(newNode);
        foreach (var leaf in leaves)
        {
            SelectedGroup.Graph.Edges.Add(new Salvo.App.ViewModels.Flow.EdgeViewModel
            {
                Id = Guid.NewGuid().ToString(),
                From = leaf.Id,
                To = newNode.Id,
            });
        }
        SelectedGroup.Graph.RebuildStages();

        if (newNode is Salvo.App.ViewModels.Flow.AppNodeViewModel app)
        {
            AppIconLoader.LoadFor([app.App]);
            RefreshRunningStates();
        }
        PersistConfig();
    }

    /// <summary>
    /// Build an AppNodeViewModel via the AppEntryEditor flow, optionally
    /// pre-populating the editor (e.g. from a picked installed app).
    /// Returns null if the user cancels.
    /// </summary>
    private Salvo.App.ViewModels.Flow.AppNodeViewModel? BuildAppNodeViaEditor(Action<AppEntryEditorViewModel>? configure = null)
    {
        var editor = _serviceProvider.GetRequiredService<AppEntryEditorViewModel>();
        editor.IsNew = true;
        configure?.Invoke(editor);

        var window = new AppEntryEditorWindow(editor);
        if (window.ShowDialog() != true) return null;

        var entry = new AppEntryViewModel();
        editor.ApplyTo(entry);
        return new Salvo.App.ViewModels.Flow.AppNodeViewModel
        {
            Id = Guid.NewGuid().ToString(),
            App = entry,
        };
    }

    /// <summary>
    /// Open the installed-app / service scanner (the same picker the old
    /// list UI used) and return the app node(s) the user chose — checked
    /// installed apps, a picked-then-edited app, or a hand-entered blank
    /// one. Empty if cancelled. Shared by every flow "App" add so the
    /// scanner is reachable again from the graph editor.
    /// </summary>
    private List<Salvo.App.ViewModels.Flow.AppNodeViewModel> PickAppNodes()
    {
        var picker = _serviceProvider.GetRequiredService<AddAppPickerViewModel>();
        var window = new AddAppPickerWindow(picker);
        window.ShowDialog();

        switch (picker.Result)
        {
            case PickerAction.AddSelected:
                return picker.GetCheckedModels().Select(ToAppNode).ToList();
            case PickerAction.EditSelected:
                var target = picker.GetSingleCheckedModel();
                var edited = target is not null ? BuildAppNodeViaEditor(e => e.LoadFromInstalled(target)) : null;
                return edited is null ? [] : [edited];
            case PickerAction.AddBlank:
                var blank = BuildAppNodeViaEditor();
                return blank is null ? [] : [blank];
            default:
                return [];
        }
    }

    private static Salvo.App.ViewModels.Flow.AppNodeViewModel ToAppNode(InstalledApp item)
    {
        var isService = item.Source == InstalledAppSource.Service;
        return new Salvo.App.ViewModels.Flow.AppNodeViewModel
        {
            Id = Guid.NewGuid().ToString(),
            App = new AppEntryViewModel
            {
                Name = item.Name,
                Kind = isService ? AppKind.Service : AppKind.Executable,
                Path = isService ? null : item.Launch,
                Service = isService ? item.ServiceName : null,
                Enabled = true,
            },
        };
    }

    /// <summary>
    /// Inserts a freshly-created node of the given <paramref name="kind"/>
    /// immediately after the given <paramref name="stage"/>, splitting
    /// any outgoing edges so the structure remains connected. Wired to
    /// the per-stage "+ Insert below" affordance.
    /// </summary>
    public void InsertNodeAfterStage(Salvo.App.ViewModels.Flow.StageViewModel? stage, string? kind)
    {
        if (SelectedGroup is null || stage is null || string.IsNullOrEmpty(kind)) return;

        // "App" opens the scanner (possibly several apps); chain them in
        // sequence starting right after the target stage.
        if (kind == "App")
        {
            var appNodes = PickAppNodes();
            Salvo.App.ViewModels.Flow.NodeViewModel? prev = null;
            foreach (var n in appNodes)
            {
                if (prev is null) SelectedGroup.Graph.InsertAfterStage(stage, n);
                else SelectedGroup.Graph.AddNodeAfter(prev, n);
                prev = n;
            }
            if (appNodes.Count > 0)
            {
                AppIconLoader.LoadFor(appNodes.Select(n => n.App).ToList());
                RefreshRunningStates();
                PersistConfig();
            }
            return;
        }

        var newNode = CreateNode(kind);
        if (newNode is null) return;

        SelectedGroup.Graph.InsertAfterStage(stage, newNode);
        if (newNode is Salvo.App.ViewModels.Flow.AppNodeViewModel app)
        {
            AppIconLoader.LoadFor([app.App]);
            RefreshRunningStates();
        }
        PersistConfig();
    }

    /// <summary>
    /// Append a freshly-created node to the "then" or "else" branch of an
    /// If node. Branches are linear sequences held on the If view-model;
    /// they're compiled into the flat then/else-labeled edge graph on save.
    /// Wired to the per-branch "+ add" affordances in the If node card.
    /// </summary>
    public void AddNodeToBranch(Salvo.App.ViewModels.Flow.IfElseNodeViewModel? ifNode, string? branch, string? kind)
    {
        if (SelectedGroup is null || ifNode is null || string.IsNullOrEmpty(kind)) return;

        var target = string.Equals(branch, "else", StringComparison.Ordinal) ? ifNode.ElseNodes : ifNode.ThenNodes;

        // "App" opens the scanner; add every chosen app to the branch.
        if (kind == "App")
        {
            var appNodes = PickAppNodes();
            foreach (var n in appNodes) target.Add(n);
            if (appNodes.Count > 0)
            {
                AppIconLoader.LoadFor(appNodes.Select(n => n.App).ToList());
                RefreshRunningStates();
                PersistConfig();
            }
            return;
        }

        var newNode = CreateNode(kind);
        if (newNode is null) return;

        target.Add(newNode);
        PersistConfig();
    }

    /// <summary>
    /// Move a node into an If node's then/else branch. Source can be an
    /// outer-graph node (detached from the chain, restitching its
    /// neighbours) or a node already in another branch (moved across).
    /// Appends to the target branch.
    /// </summary>
    public void MoveNodeIntoBranch(Salvo.App.ViewModels.Flow.NodeViewModel? dragged,
        Salvo.App.ViewModels.Flow.IfElseNodeViewModel? ifNode, string? branch)
    {
        if (SelectedGroup is null || dragged is null || ifNode is null) return;
        // No Start, no self, no nested If (the branch transform is linear).
        if (dragged is Salvo.App.ViewModels.Flow.StartNodeViewModel) return;
        if (dragged is Salvo.App.ViewModels.Flow.IfElseNodeViewModel) return;
        if (ReferenceEquals(dragged, ifNode)) return;

        if (SelectedGroup.Graph.Nodes.Contains(dragged))
        {
            SelectedGroup.Graph.RemoveNode(dragged);   // outer → branch
        }
        else if (!RemoveFromAnyBranch(dragged))
        {
            return;   // unknown origin
        }

        var target = string.Equals(branch, "else", StringComparison.Ordinal) ? ifNode.ElseNodes : ifNode.ThenNodes;
        if (!target.Contains(dragged)) target.Add(dragged);
        PersistConfig();
    }

    /// <summary>
    /// Pull a node out of whatever If branch it lives in and place it back
    /// in the outer graph (with no edges yet). The drop handler then
    /// positions it at the release point. Returns false if the node wasn't
    /// found in any branch.
    /// </summary>
    public bool ExtractBranchNodeToOuter(Salvo.App.ViewModels.Flow.NodeViewModel? node)
    {
        if (SelectedGroup is null || node is null) return false;
        if (!RemoveFromAnyBranch(node)) return false;
        SelectedGroup.Graph.Nodes.Add(node);
        return true;
    }

    private bool RemoveFromAnyBranch(Salvo.App.ViewModels.Flow.NodeViewModel node)
    {
        if (SelectedGroup is null) return false;
        foreach (var ifNode in SelectedGroup.Graph.Nodes.OfType<Salvo.App.ViewModels.Flow.IfElseNodeViewModel>())
        {
            if (ifNode.ThenNodes.Remove(node) || ifNode.ElseNodes.Remove(node)) return true;
        }
        return false;
    }

    /// <summary>
    /// Convert a parallel stage into a sequential chain. Wired to the
    /// "Make sequential" button on the parallel container header.
    /// </summary>
    [RelayCommand]
    private void MakeStageSequential(Salvo.App.ViewModels.Flow.StageViewModel? stage)
    {
        if (SelectedGroup is null || stage is null) return;
        SelectedGroup.Graph.MakeStageSequential(stage);
        PersistConfig();
    }

    [RelayCommand]
    private async Task RemoveNodeAsync(Salvo.App.ViewModels.Flow.NodeViewModel? node)
    {
        if (node is null || SelectedGroup is null) return;
        if (node is Salvo.App.ViewModels.Flow.StartNodeViewModel) return; // never remove Start

        // Structural fragments are localized; the interpolated user data
        // (app name, service name, command) is left as-is.
        var label = node switch
        {
            Salvo.App.ViewModels.Flow.AppNodeViewModel a => a.App.Name,
            Salvo.App.ViewModels.Flow.WaitNodeViewModel w => string.Format(CultureInfo.CurrentUICulture, Strings.Flow_NodeLabel_WaitFormat, w.DurationSeconds),
            Salvo.App.ViewModels.Flow.IfElseNodeViewModel => Strings.Flow_NodeLabel_IfElse,
            Salvo.App.ViewModels.Flow.ServiceStartNodeViewModel s => string.Format(CultureInfo.CurrentUICulture, Strings.Flow_NodeLabel_StartFormat, s.ServiceName),
            Salvo.App.ViewModels.Flow.ServiceStopNodeViewModel s => string.Format(CultureInfo.CurrentUICulture, Strings.Flow_NodeLabel_StopFormat, s.ServiceName),
            Salvo.App.ViewModels.Flow.RunCommandNodeViewModel c => string.Format(CultureInfo.CurrentUICulture, Strings.Flow_NodeLabel_RunFormat, c.Command),
            Salvo.App.ViewModels.Flow.GroupCallNodeViewModel => Strings.Flow_NodeLabel_GroupCall,
            _ => Strings.Flow_NodeLabel_Fallback,
        };
        if (!await _dialogs.ConfirmAsync(
            Strings.Dialog_RemoveApp_Title,
            string.Format(CultureInfo.CurrentUICulture, Strings.Dialog_RemoveApp_MessageFormat, label, SelectedGroup.Name)))
        {
            return;
        }

        // Outer-graph node → normal topology removal. Branch node (lives
        // on an If view-model, not in the outer graph) → pull it out of
        // whichever branch owns it.
        if (SelectedGroup.Graph.Nodes.Contains(node))
        {
            SelectedGroup.Graph.RemoveNode(node);
        }
        else
        {
            foreach (var ifNode in SelectedGroup.Graph.Nodes.OfType<Salvo.App.ViewModels.Flow.IfElseNodeViewModel>())
            {
                if (ifNode.ThenNodes.Remove(node) || ifNode.ElseNodes.Remove(node)) break;
            }
        }
        PersistConfig();
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var folder = Path.GetDirectoryName(_configStore.ConfigPath);
        if (!string.IsNullOrEmpty(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppPaths.LogFolder,
            UseShellExecute = true
        });
    }

    private static void ApplyResults(GroupViewModel group, IReadOnlyList<OperationResult> results)
    {
        for (var i = 0; i < Math.Min(results.Count, group.Apps.Count); i++)
        {
            group.Apps[i].LastStatus = results[i].Message;
        }
    }

    private Task HandleElevationAsync(IReadOnlyList<OperationResult> results, ElevationAction action) =>
        MaybeElevateInternalAsync(results, action);

    private Task MaybeElevateAsync(OperationResult result, ElevationAction action) =>
        MaybeElevateInternalAsync([result], action);

    private async Task MaybeElevateInternalAsync(IReadOnlyList<OperationResult> results, ElevationAction action)
    {
        var services = results
            .Where(r => r.Status == OperationStatus.NeedsElevation)
            .Select(r => r.Source?.Service)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (services.Count == 0) return;

        var messageFormat = action == ElevationAction.Start
            ? Strings.Dialog_AdministratorRequired_StartFormat
            : Strings.Dialog_AdministratorRequired_StopFormat;

        if (!await _dialogs.ConfirmAsync(
            Strings.Dialog_AdministratorRequired_Title,
            string.Format(CultureInfo.CurrentUICulture, messageFormat, string.Join(", ", services))))
        {
            return;
        }

        await _elevation.InvokeAsync(new ElevationRequest
        {
            Action = action,
            ServiceNames = services
        }).ConfigureAwait(true);
    }

    private static string Slugify(string input)
    {
        var chars = input.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}
