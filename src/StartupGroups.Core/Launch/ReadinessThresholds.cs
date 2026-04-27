namespace StartupGroups.Core.Launch;

public static class ReadinessThresholds
{
    public const double ActivityQuietCpuPercent = 5.0;
    public const double ActivityQuietIoBytesPerSecond = 1_048_576;
    public const double ActivityQuietMinMaxCpuSeen = 5.0;
}
