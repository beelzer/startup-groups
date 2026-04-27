namespace StartupGroups.Core.Launch;

public interface ILaunchBenchmarkStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LaunchMetrics metrics, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LaunchMetrics>> GetRecentAsync(string appId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LaunchMetrics>> GetAllSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);

    Task<bool> HasReadyLaunchSinceBootAsync(string appId, DateTimeOffset bootEpochUtc, CancellationToken cancellationToken = default);

    Task SaveResourcesAsync(Guid launchId, IEnumerable<string> paths, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetResourcesSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}
