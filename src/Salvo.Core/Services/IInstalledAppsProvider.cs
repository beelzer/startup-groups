using Salvo.Core.Models;

namespace Salvo.Core.Services;

public interface IInstalledAppsProvider
{
    Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default);
}
