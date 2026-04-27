namespace StartupGroups.Core.Services;

public static class AppPaths
{
    public const string AppFolderName = "StartupGroups";
    public const string ConfigFileName = "config.json";
    public const string LogFolderName = "logs";
    public const string BenchmarksDbFileName = "launch-benchmarks.db";

    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string LocalDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

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
