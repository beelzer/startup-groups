namespace StartupGroups.Core.Launch;

public enum ReadinessSignal
{
    None = 0,
    WaitForInputIdle = 1,
    MainWindowVisible = 2,
    ActivityQuiet = 3,
    ServiceRunning = 4,
    EarlyExit = 5,
    Timeout = 6,
}
