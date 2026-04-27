using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.Core.Launch;

namespace StartupGroups.App.ViewModels;

public partial class BenchmarkRowViewModel : ObservableObject
{
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _groupId = string.Empty;
    [ObservableProperty] private DateTimeOffset _requestedAt;
    [ObservableProperty] private string _durationDisplay = string.Empty;
    [ObservableProperty] private string _outcomeDisplay = string.Empty;
    [ObservableProperty] private string _signalDisplay = string.Empty;
    [ObservableProperty] private string _coldWarm = string.Empty;
    [ObservableProperty] private int? _rootPid;
    [ObservableProperty] private string _whenDisplay = string.Empty;
    [ObservableProperty] private LaunchOutcome _outcome;
    [ObservableProperty] private bool _isCold;
    [ObservableProperty] private bool _isRegression;
    [ObservableProperty] private string _regressionText = string.Empty;

    public static BenchmarkRowViewModel FromMetrics(LaunchMetrics m) => new()
    {
        AppName = m.AppName ?? m.AppId,
        GroupId = m.GroupId ?? "",
        RequestedAt = m.RequestedAt,
        WhenDisplay = FormatWhen(m.RequestedAt),
        DurationDisplay = FormatDuration(m),
        OutcomeDisplay = m.Outcome.ToString(),
        SignalDisplay = FormatSignal(m.SignalFired),
        ColdWarm = m.IsCold ? "cold" : "warm",
        IsCold = m.IsCold,
        RootPid = m.RootPid,
        Outcome = m.Outcome,
    };

    private static string FormatWhen(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatDuration(LaunchMetrics m)
    {
        if (m.TotalDuration is not TimeSpan d) return "-";
        return d.TotalMilliseconds < 1000
            ? $"{d.TotalMilliseconds:F0}ms"
            : string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:F2}s");
    }

    private static string FormatSignal(ReadinessSignal s) => s switch
    {
        ReadinessSignal.MainWindowVisible => "main window",
        ReadinessSignal.WaitForInputIdle => "input idle",
        ReadinessSignal.ActivityQuiet => "quiet",
        ReadinessSignal.ServiceRunning => "service",
        ReadinessSignal.EarlyExit => "early exit",
        ReadinessSignal.Timeout => "timeout",
        _ => "-",
    };
}
