using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IInstalledAppsProvider
{
    Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default);
}
