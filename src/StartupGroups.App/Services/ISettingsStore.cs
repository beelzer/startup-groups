namespace StartupGroups.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum AppsViewMode
{
    Compact,
    Detailed
}

public enum UpdateChannel
{
    Stable,
    Canary,
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public AppsViewMode AppsViewMode { get; set; } = AppsViewMode.Detailed;

    /// <summary>Empty string = follow system UI language. Otherwise a culture name like "en", "fr", "ja".</summary>
    public string UiCulture { get; set; } = string.Empty;

    public bool AlwaysRunAsAdmin { get; set; }

    public bool WarnWhenElevatedAppsPresent { get; set; } = true;

    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>
    /// When true (default), launching StartupGroups.exe opens the main window
    /// in addition to the tray icon. When false, the app starts tray-only.
    /// </summary>
    public bool ShowMainWindowOnLaunch { get; set; } = true;
}

public interface ISettingsStore
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    void Save(AppSettings settings);
}
