namespace StartupGroups.Core.Launch;

public static class BenchmarkPolicy
{
    public static readonly TimeSpan RunClusterGap = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    public const int RecentLaunchesLimit = 100;
    public const int GroupRunsDisplayLimit = 20;

    public const int DependencyMinEdgeRuns = 2;
    public const int DependencyMinRunsForSuggestion = 2;

    public const double RegressionRatio = 2.0;
    public const int RegressionMinSampleSize = 3;
}
