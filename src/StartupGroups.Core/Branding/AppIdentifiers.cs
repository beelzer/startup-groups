namespace StartupGroups.Core.Branding;

public static class AppIdentifiers
{
    public const string TrayTaskName = "StartupGroupsTray";
    public const string TrayCommandLineFlag = "--tray";

    public const string ElevatorExecutableName = "StartupGroups.Elevator.exe";
    public const string MainExecutableName = "StartupGroups.exe";

    public const string AssetsFolderName = "Assets";
    public const string TrayIconLightFileName = "tray-light.ico";
    public const string TrayIconDarkFileName = "tray-dark.ico";
    public const string AppIconFileName = "app.ico";

    // Stable AppUserModelID. Without this, Windows auto-derives a per-launch
    // identity from the executable path and the running window doesn't match
    // any pinned shortcut — taskbar icon falls back to the small frame and the
    // running app gets a separate taskbar slot from the pinned one.
    public const string AppUserModelId = "StartupGroups.App";

    public const string LogFileRollingPattern = "app-.log";

    public const string InstallRegistryKey = @"Software\Startup Groups";
    public const string WindowsPersonalizeRegistryKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
}
