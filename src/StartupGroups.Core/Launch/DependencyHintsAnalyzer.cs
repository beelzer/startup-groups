using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class DependencyHintsAnalyzer
{
    private static readonly TimeSpan RunClusterGap = BenchmarkPolicy.RunClusterGap;
    private const int MinEdgeRuns = BenchmarkPolicy.DependencyMinEdgeRuns;
    private const int MinRunsForSuggestion = BenchmarkPolicy.DependencyMinRunsForSuggestion;

    private readonly ILaunchBenchmarkStore _store;
    private readonly ILogger<DependencyHintsAnalyzer> _logger;

    public DependencyHintsAnalyzer(ILaunchBenchmarkStore store, ILogger<DependencyHintsAnalyzer>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<DependencyHintsAnalyzer>.Instance;
    }

    public async Task<IReadOnlyList<DependencyHint>> AnalyzeAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        var all = await _store.GetAllSinceAsync(sinceUtc, cancellationToken).ConfigureAwait(false);
        var resources = await _store.GetResourcesSinceAsync(sinceUtc, cancellationToken).ConfigureAwait(false);

        var byGroup = all
            .Where(m => !string.IsNullOrEmpty(m.GroupId))
            .GroupBy(m => m.GroupId!)
            .ToList();

        var hints = new List<DependencyHint>();
        foreach (var groupBatch in byGroup)
        {
            var runs = ClusterRuns(groupBatch.ToList());
            if (runs.Count < MinRunsForSuggestion) continue;

            var (edges, appNames, latestOrder) = ComputeEdges(runs, resources);
            if (latestOrder.Count == 0) continue;

            var suggested = TopologicalOrder(latestOrder, edges);
            hints.Add(new DependencyHint(
                GroupId: groupBatch.Key,
                CurrentOrder: latestOrder,
                SuggestedOrder: suggested,
                AppIdToName: appNames,
                Edges: edges));
        }

        return hints;
    }

    private static IReadOnlyList<IReadOnlyList<LaunchMetrics>> ClusterRuns(IReadOnlyList<LaunchMetrics> group)
    {
        var ordered = group.OrderBy(m => m.RequestedAt).ToList();
        var runs = new List<List<LaunchMetrics>>();
        List<LaunchMetrics>? current = null;
        DateTimeOffset? lastAt = null;

        foreach (var m in ordered)
        {
            if (current is null || (lastAt is DateTimeOffset prev && (m.RequestedAt - prev) > RunClusterGap))
            {
                current = new List<LaunchMetrics>();
                runs.Add(current);
            }
            current.Add(m);
            lastAt = m.RequestedAt;
        }

        return runs;
    }

    private static (IReadOnlyList<DependencyEdge> Edges, IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> LatestOrder) ComputeEdges(
        IReadOnlyList<IReadOnlyList<LaunchMetrics>> runs,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> resources)
    {
        var edgeCounts = new Dictionary<(string From, string To), int>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var run in runs)
        {
            foreach (var m in run)
            {
                if (!names.ContainsKey(m.AppId) && !string.IsNullOrEmpty(m.AppName))
                {
                    names[m.AppId] = m.AppName!;
                }
            }

            for (var i = 0; i < run.Count; i++)
            {
                var a = run[i];
                if (!resources.TryGetValue(a.LaunchId, out var aPaths) || aPaths.Count == 0) continue;
                var aPathSet = new HashSet<string>(aPaths, StringComparer.OrdinalIgnoreCase);
                var aReady = a.ReadyAt ?? a.RequestedAt;

                for (var j = 0; j < run.Count; j++)
                {
                    if (i == j) continue;
                    var b = run[j];
                    if (a.AppId == b.AppId) continue;
                    if (b.RequestedAt <= aReady) continue;
                    if (!resources.TryGetValue(b.LaunchId, out var bPaths)) continue;
                    if (!bPaths.Any(p => aPathSet.Contains(p))) continue;

                    var key = (a.AppId, b.AppId);
                    edgeCounts[key] = edgeCounts.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
        }

        var edges = edgeCounts
            .Where(kv => kv.Value >= MinEdgeRuns)
            .Select(kv => new DependencyEdge(kv.Key.From, kv.Key.To, kv.Value))
            .ToList();

        var latestRun = runs[runs.Count - 1]
            .OrderBy(m => m.RequestedAt)
            .Select(m => m.AppId)
            .Distinct()
            .ToList();

        return (edges, names, latestRun);
    }

    private static IReadOnlyList<string> TopologicalOrder(IReadOnlyList<string> currentOrder, IReadOnlyList<DependencyEdge> edges)
    {
        var edgeSet = new HashSet<(string, string)>(edges.Select(e => (e.FromAppId, e.ToAppId)));

        var result = new List<string>(currentOrder.Count);
        var remaining = new List<string>(currentOrder);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            string? pick = null;
            foreach (var candidate in remaining)
            {
                var hasUnplacedPredecessor = remaining.Any(other =>
                    other != candidate && edgeSet.Contains((other, candidate)) && !placed.Contains(other));
                if (!hasUnplacedPredecessor)
                {
                    pick = candidate;
                    break;
                }
            }
            pick ??= remaining[0];
            result.Add(pick);
            placed.Add(pick);
            remaining.Remove(pick);
        }

        return result;
    }
}
