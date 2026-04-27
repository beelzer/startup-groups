using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StartupGroups.App.Services;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.App.ViewModels;

public partial class BenchmarksViewModel : ObservableObject
{
    private const int RecentLimit = BenchmarkPolicy.RecentLaunchesLimit;

    private readonly ILaunchBenchmarkStore? _store;
    private readonly ILaunchTelemetryService? _telemetry;
    private readonly DependencyHintsAnalyzer? _analyzer;
    private readonly IConfigStore? _configStore;
    private readonly ILogger<BenchmarksViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private int _timedOutCount;
    [ObservableProperty] private int _coldCount;
    [ObservableProperty] private string _medianDurationDisplay = "-";
    [ObservableProperty] private string _coldMedianDisplay = "-";
    [ObservableProperty] private string _warmMedianDisplay = "-";
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<BenchmarkRowViewModel> Recent { get; } = [];
    public ObservableCollection<AppBenchmarkSummaryViewModel> PerApp { get; } = [];
    public ObservableCollection<GroupRunSummaryViewModel> GroupRuns { get; } = [];
    public ObservableCollection<DependencyHintViewModel> Hints { get; } = [];

    public BenchmarksViewModel(
        ILogger<BenchmarksViewModel> logger,
        ILaunchBenchmarkStore? store = null,
        ILaunchTelemetryService? telemetry = null,
        DependencyHintsAnalyzer? analyzer = null,
        IConfigStore? configStore = null)
    {
        _store = store;
        _telemetry = telemetry;
        _analyzer = analyzer;
        _configStore = configStore;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        if (_telemetry is not null)
        {
            _telemetry.MetricsSaved += (_, _) => _ = RefreshAsync();
        }
    }

    [RelayCommand]
    public void ApplySuggestedOrder(DependencyHintViewModel? hint)
    {
        if (hint is null || _configStore is null) return;
        if (!hint.IsReorderSuggested) return;

        try
        {
            var config = _configStore.Load();
            var group = config.Groups.FirstOrDefault(g => string.Equals(g.Id, hint.GroupId, StringComparison.Ordinal));
            if (group is null)
            {
                _logger.LogDebug("Apply suggested order: group {Group} not found", hint.GroupId);
                return;
            }

            var rankByAppId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < hint.SuggestedAppIds.Count; i++)
            {
                rankByAppId[hint.SuggestedAppIds[i]] = i;
            }

            var reordered = group.Apps
                .Select(app => new
                {
                    App = app,
                    Rank = rankByAppId.TryGetValue(AppIdentity.ComputeAppId(app.Path, app.Name), out var r) ? r : int.MaxValue,
                })
                .OrderBy(x => x.Rank)
                .ThenBy(x => group.Apps.IndexOf(x.App))
                .Select(x => x.App)
                .ToList();

            var updated = new Configuration
            {
                Version = config.Version,
                Groups = config.Groups.Select(g => ReferenceEquals(g, group)
                    ? new Group { Id = g.Id, Name = g.Name, Icon = g.Icon, Apps = reordered }
                    : g).ToList(),
            };

            _configStore.Save(updated);
            _logger.LogInformation("Applied suggested order for group {Group}", hint.GroupId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApplySuggestedOrder failed for group {Group}", hint.GroupId);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_store is null) return;

        try
        {
            IsLoading = true;
            var since = DateTimeOffset.UtcNow - BenchmarkPolicy.RetentionWindow;
            var all = await _store.GetAllSinceAsync(since).ConfigureAwait(true);

            var iconSourceByAppId = BuildIconSourceByAppId();

            _dispatcher.Invoke(() =>
            {
                var byApp = all.GroupBy(m => m.AppId).ToList();
                var summariesByAppId = new Dictionary<string, AppBenchmarkSummaryViewModel>(StringComparer.Ordinal);
                PerApp.Clear();
                foreach (var g in byApp.OrderByDescending(g => g.Max(m => m.RequestedAt)))
                {
                    var summary = AppBenchmarkSummaryViewModel.FromMetrics(g.Key, g.ToList());
                    if (iconSourceByAppId.TryGetValue(g.Key, out var iconSource))
                    {
                        summary.IconSource = iconSource;
                    }
                    summariesByAppId[g.Key] = summary;
                    PerApp.Add(summary);
                }

                if (OperatingSystem.IsWindows())
                {
                    LoadPerAppIcons(PerApp);
                }

                Recent.Clear();
                foreach (var m in all.OrderByDescending(m => m.RequestedAt).Take(RecentLimit))
                {
                    var row = BenchmarkRowViewModel.FromMetrics(m);
                    if (summariesByAppId.TryGetValue(m.AppId, out var summary) &&
                        m.Outcome == LaunchOutcome.Ready &&
                        m.TotalDuration is TimeSpan dur)
                    {
                        var reference = m.IsCold ? summary.ColdMedianRaw : summary.WarmMedianRaw;
                        if (reference is TimeSpan refDur && refDur.TotalMilliseconds > 0)
                        {
                            var ratio = dur.TotalMilliseconds / refDur.TotalMilliseconds;
                            if (ratio >= BenchmarkPolicy.RegressionRatio && summary.ReadyCount >= BenchmarkPolicy.RegressionMinSampleSize)
                            {
                                row.IsRegression = true;
                                row.RegressionText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ratio:F1}x");
                            }
                        }
                    }
                    Recent.Add(row);
                }

                TotalCount = all.Count;
                var ready = all.Where(m => m.Outcome == LaunchOutcome.Ready && m.TotalDuration is not null).ToList();
                ReadyCount = ready.Count;
                TimedOutCount = all.Count(m => m.Outcome == LaunchOutcome.TimedOut);
                ColdCount = all.Count(m => m.IsCold);
                MedianDurationDisplay = FormatMedian(ready.Select(m => m.TotalDuration!.Value).ToList());
                ColdMedianDisplay = FormatMedian(ready.Where(m => m.IsCold).Select(m => m.TotalDuration!.Value).ToList());
                WarmMedianDisplay = FormatMedian(ready.Where(m => !m.IsCold).Select(m => m.TotalDuration!.Value).ToList());

                GroupRuns.Clear();
                foreach (var run in BuildGroupRuns(all).Take(BenchmarkPolicy.GroupRunsDisplayLimit))
                {
                    GroupRuns.Add(run);
                }
            });

            if (_analyzer is not null)
            {
                try
                {
                    var hints = await _analyzer.AnalyzeAsync(since).ConfigureAwait(true);
                    _dispatcher.Invoke(() =>
                    {
                        Hints.Clear();
                        foreach (var h in hints)
                        {
                            Hints.Add(DependencyHintViewModel.FromHint(h));
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dependency analysis failed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load benchmarks");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Dictionary<string, string> BuildIconSourceByAppId()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_configStore is null) return map;
        try
        {
            var config = _configStore.Load();
            foreach (var group in config.Groups)
            {
                foreach (var app in group.Apps)
                {
                    var id = AppIdentity.ComputeAppId(app.Path, app.Name);
                    var source = app.Kind == AppKind.Executable && !string.IsNullOrWhiteSpace(app.Path)
                        ? app.Path
                        : app.Kind == AppKind.Service && !string.IsNullOrWhiteSpace(app.Service)
                            ? WindowsServicesProvider.TryResolveImagePath(app.Service!)
                            : null;
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        map[id] = source!;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BuildIconSourceByAppId failed");
        }
        return map;
    }

    [SupportedOSPlatform("windows")]
    private void LoadPerAppIcons(IEnumerable<AppBenchmarkSummaryViewModel> summaries)
    {
        var targets = summaries
            .Where(s => s.Icon is null && !string.IsNullOrWhiteSpace(s.IconSource))
            .Select(s => (vm: s, source: s.IconSource!))
            .ToList();
        if (targets.Count == 0) return;

        var dispatcher = _dispatcher;
        var thread = new Thread(() =>
        {
            foreach (var (vm, source) in targets)
            {
                try
                {
                    var icon = AppIconCache.Get(source);
                    if (icon is not null)
                    {
                        dispatcher.BeginInvoke(() => vm.Icon = icon, DispatcherPriority.Background);
                    }
                }
                catch
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "BenchmarkIcon-STA",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static readonly TimeSpan RunClusterGap = BenchmarkPolicy.RunClusterGap;

    private IEnumerable<GroupRunSummaryViewModel> BuildGroupRuns(IReadOnlyList<LaunchMetrics> all)
    {
        var groupsById = _configStore?.Load().Groups.ToDictionary(g => g.Id, g => g, StringComparer.Ordinal)
                         ?? new Dictionary<string, Group>(StringComparer.Ordinal);

        var byGroup = all
            .Where(m => !string.IsNullOrEmpty(m.GroupId))
            .GroupBy(m => m.GroupId!)
            .ToList();

        var runs = new List<GroupRunSummaryViewModel>();
        foreach (var g in byGroup)
        {
            groupsById.TryGetValue(g.Key, out var group);
            var name = group?.Name;
            var icon = group?.Icon;

            var ordered = g.OrderBy(m => m.RequestedAt).ToList();
            var currentRun = new List<LaunchMetrics>();
            DateTimeOffset? lastAt = null;
            foreach (var m in ordered)
            {
                if (lastAt is DateTimeOffset prev && (m.RequestedAt - prev) > RunClusterGap)
                {
                    if (currentRun.Count > 0) runs.Add(GroupRunSummaryViewModel.FromRun(g.Key, name, icon, currentRun));
                    currentRun = new List<LaunchMetrics>();
                }
                currentRun.Add(m);
                lastAt = m.RequestedAt;
            }
            if (currentRun.Count > 0) runs.Add(GroupRunSummaryViewModel.FromRun(g.Key, name, icon, currentRun));
        }

        return runs.OrderByDescending(r => r.StartedAt);
    }

    private static string FormatMedian(IReadOnlyList<TimeSpan> values)
    {
        if (values.Count == 0) return "-";
        var sorted = values.OrderBy(v => v.Ticks).ToArray();
        var mid = sorted.Length / 2;
        var median = sorted.Length % 2 == 0
            ? TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2)
            : sorted[mid];
        return median.TotalMilliseconds < 1000
            ? $"{median.TotalMilliseconds:F0}ms"
            : string.Create(CultureInfo.InvariantCulture, $"{median.TotalSeconds:F2}s");
    }
}
