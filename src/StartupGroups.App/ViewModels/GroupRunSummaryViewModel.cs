using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.Core.Launch;

namespace StartupGroups.App.ViewModels;

public partial class GroupRunSummaryViewModel : ObservableObject
{
    [ObservableProperty] private string _groupId = string.Empty;
    [ObservableProperty] private string _groupName = string.Empty;
    [ObservableProperty] private string _groupIcon = "Apps24";
    [ObservableProperty] private DateTimeOffset _startedAt;
    [ObservableProperty] private string _startedDisplay = string.Empty;
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _totalDurationDisplay = "-";
    [ObservableProperty] private string _bottleneckApp = string.Empty;
    [ObservableProperty] private string _bottleneckDurationDisplay = string.Empty;

    public ObservableCollection<GroupRunAppBarViewModel> Bars { get; } = [];

    public static GroupRunSummaryViewModel FromRun(
        string groupId,
        string? groupName,
        string? groupIcon,
        IReadOnlyList<LaunchMetrics> runLaunches)
    {
        var ordered = runLaunches.OrderBy(m => m.RequestedAt).ToList();
        var startedAt = ordered.First().RequestedAt;
        var endedAt = ordered
            .Select(m => m.ReadyAt ?? m.RequestedAt)
            .Max();
        var totalDuration = endedAt - startedAt;

        // Bottleneck = the app that gates the group — i.e. the one that finishes last.
        // Using longest individual duration is misleading when apps run in parallel,
        // because the group total can exceed any single app's duration.
        var bottleneck = ordered
            .Where(m => m.Outcome == LaunchOutcome.Ready && m.ReadyAt is not null)
            .OrderByDescending(m => m.ReadyAt!.Value)
            .FirstOrDefault();

        var vm = new GroupRunSummaryViewModel
        {
            GroupId = groupId,
            GroupName = string.IsNullOrWhiteSpace(groupName) ? groupId : groupName,
            GroupIcon = string.IsNullOrWhiteSpace(groupIcon) ? "Apps24" : groupIcon,
            StartedAt = startedAt,
            StartedDisplay = startedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            AppCount = ordered.Count,
            TotalDurationDisplay = FormatDuration(totalDuration),
            BottleneckApp = bottleneck?.AppName ?? "-",
            BottleneckDurationDisplay = bottleneck?.TotalDuration is TimeSpan d ? FormatDuration(d) : "-",
        };

        var totalMs = Math.Max(totalDuration.TotalMilliseconds, 1);
        foreach (var m in ordered)
        {
            var offsetMs = (m.RequestedAt - startedAt).TotalMilliseconds;
            var durMs = (m.ReadyAt is DateTimeOffset ra ? (ra - m.RequestedAt).TotalMilliseconds : 0);
            vm.Bars.Add(new GroupRunAppBarViewModel
            {
                AppName = m.AppName ?? m.AppId,
                OffsetFraction = offsetMs / totalMs,
                WidthFraction = Math.Max(durMs / totalMs, 0.005),
                DurationDisplay = m.TotalDuration is TimeSpan dd ? FormatDuration(dd) : "-",
                OutcomeDisplay = m.Outcome.ToString(),
                SignalDisplay = m.SignalFired.ToString(),
                IsReady = m.Outcome == LaunchOutcome.Ready,
                IsCold = m.IsCold,
            });
        }

        return vm;
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMilliseconds < 1000
            ? $"{d.TotalMilliseconds:F0}ms"
            : string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:F2}s");
}

public partial class GroupRunAppBarViewModel : ObservableObject
{
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private double _offsetFraction;
    [ObservableProperty] private double _widthFraction;
    [ObservableProperty] private string _durationDisplay = string.Empty;
    [ObservableProperty] private string _outcomeDisplay = string.Empty;
    [ObservableProperty] private string _signalDisplay = string.Empty;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isCold;
}
