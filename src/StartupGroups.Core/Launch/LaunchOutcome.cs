namespace StartupGroups.Core.Launch;

public enum LaunchOutcome
{
    Unknown = 0,
    Ready = 1,
    TimedOut = 2,
    ExitedEarly = 3,
    PidNotFound = 4,
    Failed = 5,
}
