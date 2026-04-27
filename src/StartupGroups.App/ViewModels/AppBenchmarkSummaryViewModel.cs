using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.Core.Launch;

namespace StartupGroups.App.ViewModels;

public partial class AppBenchmarkSummaryViewModel : ObservableObject
{
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private int _timedOutCount;
    [ObservableProperty] private int _coldCount;
    [ObservableProperty] private string _medianDisplay = "-";
    [ObservableProperty] private string _coldMedianDisplay = "-";
    [ObservableProperty] private string _warmMedianDisplay = "-";
    [ObservableProperty] private string _lastDurationDisplay = "-";
    [ObservableProperty] private DateTimeOffset? _lastLaunchedAt;
    [ObservableProperty] private bool _isRegression;
    [ObservableProperty] private string _regressionText = string.Empty;
    [ObservableProperty] private BitmapSource? _icon;
    public string? IconSource { get; set; }

    public TimeSpan? WarmMedianRaw { get; private set; }
    public TimeSpan? ColdMedianRaw { get; private set; }

    public static AppBenchmarkSummaryViewModel FromMetrics(string appId, IReadOnlyList<LaunchMetrics> history)
    {
        var ready = history.Where(m => m.Outcome == LaunchOutcome.Ready && m.TotalDuration is not null).ToList();
        var warmDurations = ready.Where(m => !m.IsCold).Select(m => m.TotalDuration!.Value).ToList();
        var coldDurations = ready.Where(m => m.IsCold).Select(m => m.TotalDuration!.Value).ToList();
        var allDurations = ready.Select(m => m.TotalDuration!.Value).ToList();
        var latest = history.OrderByDescending(m => m.RequestedAt).FirstOrDefault();
        var coldMedian = Median(coldDurations);
        var warmMedian = Median(warmDurations);

        var isRegression = false;
        var regressionText = string.Empty;
        if (latest is not null && latest.Outcome == LaunchOutcome.Ready && latest.TotalDuration is TimeSpan lastDur)
        {
            var reference = latest.IsCold ? coldMedian : warmMedian;
            if (reference is TimeSpan refDur && refDur.TotalMilliseconds > 0)
            {
                var ratio = lastDur.TotalMilliseconds / refDur.TotalMilliseconds;
                if (ratio >= 2.0 && ready.Count >= 3)
                {
                    isRegression = true;
                    regressionText = string.Create(CultureInfo.InvariantCulture, $"{ratio:F1}x slower than median");
                }
            }
        }

        return new AppBenchmarkSummaryViewModel
        {
            AppId = appId,
            AppName = latest?.AppName ?? appId,
            TotalCount = history.Count,
            ReadyCount = ready.Count,
            TimedOutCount = history.Count(m => m.Outcome == LaunchOutcome.TimedOut),
            ColdCount = history.Count(m => m.IsCold),
            MedianDisplay = FormatMedian(allDurations),
            ColdMedianDisplay = FormatMedian(coldDurations),
            WarmMedianDisplay = FormatMedian(warmDurations),
            ColdMedianRaw = coldMedian,
            WarmMedianRaw = warmMedian,
            LastDurationDisplay = latest?.TotalDuration is TimeSpan ld ? FormatDuration(ld) : "-",
            LastLaunchedAt = latest?.RequestedAt,
            IsRegression = isRegression,
            RegressionText = regressionText,
        };
    }

    private static TimeSpan? Median(IReadOnlyList<TimeSpan> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v.Ticks).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2)
            : sorted[mid];
    }

    private static string FormatMedian(IReadOnlyList<TimeSpan> values)
    {
        if (values.Count == 0) return "-";
        var sorted = values.OrderBy(v => v.Ticks).ToArray();
        var mid = sorted.Length / 2;
        var median = sorted.Length % 2 == 0
            ? TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2)
            : sorted[mid];
        return FormatDuration(median);
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMilliseconds < 1000
            ? $"{d.TotalMilliseconds:F0}ms"
            : string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:F2}s");
}
