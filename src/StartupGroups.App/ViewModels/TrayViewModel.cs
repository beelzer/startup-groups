using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StartupGroups.App.Resources;
using StartupGroups.App.Views;
using StartupGroups.Core.Branding;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.App.ViewModels;

public partial class TrayViewModel : ObservableObject, IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly MainWindowViewModel _mainViewModel;
    private readonly ILogger<TrayViewModel> _logger;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;

    public TrayViewModel(
        IConfigStore configStore,
        IServiceProvider serviceProvider,
        MainWindowViewModel mainViewModel,
        ILogger<TrayViewModel> logger)
    {
        _configStore = configStore;
        _serviceProvider = serviceProvider;
        _mainViewModel = mainViewModel;
        _logger = logger;

        _configStore.Changed += (_, _) => Application.Current.Dispatcher.Invoke(RebuildGroups);
        RebuildGroups();
    }

    public ObservableCollection<TrayGroupItem> TrayGroups { get; } = [];

    public ICommand ShowMainWindowCommand => new RelayCommand(ShowMainWindow);
    public ICommand ExitCommand => new RelayCommand(Exit);

    public void Initialize()
    {
        var showCommand = new RelayCommand(ShowMainWindow);
        _trayIcon = new TaskbarIcon
        {
            IconSource = LoadTrayIcon(),
            ToolTipText = Strings.Tray_Tooltip,
            ContextMenu = TrayMenuFactory.Build(this),
            LeftClickCommand = showCommand,
            DoubleClickCommand = showCommand,
            NoLeftClickDelay = true
        };
        _trayIcon.ForceCreate();
        _logger.LogInformation("Tray icon created");
    }

    private void RebuildGroups()
    {
        TrayGroups.Clear();
        foreach (var group in _configStore.Load().Groups)
        {
            TrayGroups.Add(new TrayGroupItem(group, this));
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_trayIcon is not null)
            {
                _trayIcon.ContextMenu = TrayMenuFactory.Build(this);
            }
        });
    }

    public async Task LaunchGroupFromTrayAsync(Group model)
    {
        try
        {
            var groupVm = _mainViewModel.Groups.FirstOrDefault(g => g.Id == model.Id);
            if (groupVm is not null)
            {
                await _mainViewModel.LaunchGroupCommand.ExecuteAsync(groupVm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray launch failed");
        }
    }

    public async Task StopGroupFromTrayAsync(Group model)
    {
        try
        {
            var groupVm = _mainViewModel.Groups.FirstOrDefault(g => g.Id == model.Id);
            if (groupVm is not null)
            {
                await _mainViewModel.StopGroupCommand.ExecuteAsync(groupVm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray stop failed");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null || !_mainWindow.IsLoaded)
        {
            _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private static void Exit()
    {
        Application.Current.Shutdown();
    }

    private static BitmapImage LoadTrayIcon()
    {
        var baseDir = AppContext.BaseDirectory;
        var iconName = IsSystemInDarkMode() ? AppIdentifiers.TrayIconLightFileName : AppIdentifiers.TrayIconDarkFileName;
        var path = Path.Combine(baseDir, AppIdentifiers.AssetsFolderName, iconName);
        if (!File.Exists(path))
        {
            path = Path.Combine(baseDir, AppIdentifiers.AssetsFolderName, AppIdentifiers.AppIconFileName);
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppIdentifiers.WindowsPersonalizeRegistryKey);
            if (key?.GetValue("SystemUsesLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch
        {
        }
        return true;
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}

public sealed class TrayGroupItem(Group model, TrayViewModel parent)
{
    public Group Model { get; } = model;

    public string Name => Model.Name;

    public IAsyncRelayCommand LaunchCommand { get; } = new AsyncRelayCommand(() => parent.LaunchGroupFromTrayAsync(model));
    public IAsyncRelayCommand StopCommand { get; } = new AsyncRelayCommand(() => parent.StopGroupFromTrayAsync(model));
}
