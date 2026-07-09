using Salvo.Core.Models;

namespace Salvo.Core.Services;

public sealed class CompositeInstalledAppsProvider : IInstalledAppsProvider
{
    private readonly IReadOnlyList<IInstalledAppsProvider> _providers;

    public CompositeInstalledAppsProvider(IEnumerable<IInstalledAppsProvider> providers)
    {
        _providers = providers.ToList();
    }

    public async Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        // The wired providers (shell COM enumeration, service enumeration, Scoop
        // filesystem scan) are independent, so run them concurrently rather than
        // letting their latencies add up on the app-picker path. Cancellation is
        // propagated into each provider.
        var results = await Task.WhenAll(
            _providers.Select(p => p.EnumerateAsync(cancellationToken))).ConfigureAwait(false);

        var combined = new List<InstalledApp>(results.Sum(r => r.Count));
        foreach (var items in results)
        {
            combined.AddRange(items);
        }

        combined.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return combined;
    }
}
