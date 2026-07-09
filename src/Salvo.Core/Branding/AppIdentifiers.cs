namespace Salvo.Core.Branding;

public static class AppIdentifiers
{
    public const string TrayTaskName = "SalvoTray";
    public const string TrayCommandLineFlag = "--tray";

    // Cross-process flag: ElevationClient passes it to the elevator, which
    // parses it back. Kept in one place so the two sides can't drift.
    public const string PayloadCommandLineFlag = "--payload";

    public const string ElevatorExecutableName = "Salvo.Elevator.exe";

    public const string AssetsFolderName = "Assets";
    public const string TrayIconLightFileName = "tray-light.ico";
    public const string TrayIconDarkFileName = "tray-dark.ico";
    public const string AppIconFileName = "app.ico";

    // Stable AppUserModelID. Without this, Windows auto-derives a per-launch
    // identity from the executable path and the running window doesn't match
    // any pinned shortcut — taskbar icon falls back to the small frame and the
    // running app gets a separate taskbar slot from the pinned one.
    public const string AppUserModelId = "Salvo.App";

    public const string LogFileRollingPattern = "app-.log";

    public const string InstallRegistryKey = @"Software\Salvo";
    public const string WindowsPersonalizeRegistryKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
}
