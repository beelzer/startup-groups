namespace StartupGroups.Core.Launch;

public static class BootSession
{
    private static readonly DateTimeOffset _bootEpochUtc = ComputeBootEpoch();

    public static DateTimeOffset BootEpochUtc => _bootEpochUtc;

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static DateTimeOffset ComputeBootEpoch()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return DateTimeOffset.UtcNow - uptime;
    }
}
