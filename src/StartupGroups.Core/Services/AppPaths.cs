namespace StartupGroups.Core.Services;

public static class AppPaths
{
    public const string AppFolderName = "StartupGroups";

    /// <summary>
    /// Local-data folder name. Distinct from <see cref="AppFolderName"/> on
    /// purpose: Velopack installs to <c>%LocalAppData%\StartupGroups\</c>
    /// (it derives from the AppId we pass to <c>vpk pack -u StartupGroups</c>),
    /// so storing our cache/logs/benchmarks under that exact path collides
    /// with the install root and trips Velopack's "already installed"
    /// heuristic on fresh-machine installs. Use a dedicated namespace.
    /// </summary>
    public const string LocalDataFolderName = "StartupGroups.UserData";

    public const string ConfigFileName = "config.json";
    public const string LogFolderName = "logs";
    public const string BenchmarksDbFileName = "launch-benchmarks.db";

    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string LocalDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalDataFolderName);

    public static string ConfigFilePath =>
        Path.Combine(UserDataFolder, ConfigFileName);

    public static string LogFolder =>
        Path.Combine(LocalDataFolder, LogFolderName);

    public static string BenchmarksDbPath =>
        Path.Combine(LocalDataFolder, BenchmarksDbFileName);

    public static void EnsureUserDirectories()
    {
        Directory.CreateDirectory(UserDataFolder);
        Directory.CreateDirectory(LocalDataFolder);
        Directory.CreateDirectory(LogFolder);
    }
}
