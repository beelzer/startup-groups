using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public sealed class CompositeInstalledAppsProvider : IInstalledAppsProvider
{
    private readonly IReadOnlyList<IInstalledAppsProvider> _providers;

    public CompositeInstalledAppsProvider(IEnumerable<IInstalledAppsProvider> providers)
    {
        _providers = providers.ToList();
    }

    public async Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var combined = new List<InstalledApp>();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await provider.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            combined.AddRange(items);
        }

        combined.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return combined;
    }
}
