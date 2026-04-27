using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Models;

namespace StartupGroups.App.ViewModels;

public partial class AppEntryViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private AppKind _kind = AppKind.Executable;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _service;
    [ObservableProperty] private string? _args;
    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private int _delayAfterSeconds;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _lastStatus = string.Empty;
    [ObservableProperty] private BitmapSource? _icon;

    [ObservableProperty] private string _lastDurationDisplay = string.Empty;
    [ObservableProperty] private bool _lastIsCold;
    [ObservableProperty] private bool _hasBenchmark;
    [ObservableProperty] private string _lastBenchmarkTooltip = string.Empty;
    [ObservableProperty] private LaunchOutcome _lastOutcome = LaunchOutcome.Unknown;
    [ObservableProperty] private ReadinessSignal _lastSignal = ReadinessSignal.None;

    public bool IsStopped => !IsRunning;

    public string ComputedAppId => AppIdentity.ComputeAppId(Path, Name);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsStopped));

    public void ApplyMetrics(LaunchMetrics metrics)
    {
        LastOutcome = metrics.Outcome;
        LastSignal = metrics.SignalFired;
        LastIsCold = metrics.IsCold;
        LastDurationDisplay = FormatDisplay(metrics);
        LastBenchmarkTooltip = FormatTooltip(metrics);
        HasBenchmark = true;
    }

    private static string FormatDisplay(LaunchMetrics m) => m.Outcome switch
    {
        LaunchOutcome.Ready when m.TotalDuration is TimeSpan d => FormatDuration(d),
        LaunchOutcome.TimedOut => "timeout",
        LaunchOutcome.ExitedEarly => "exited",
        LaunchOutcome.PidNotFound => "no pid",
        LaunchOutcome.Failed => "failed",
        _ => string.Empty,
    };

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMilliseconds < 1000
            ? $"{d.TotalMilliseconds:F0}ms"
            : string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:F1}s");

    private static string FormatTooltip(LaunchMetrics m)
    {
        var coldMark = m.IsCold ? "cold" : "warm";
        var signalText = m.SignalFired switch
        {
            ReadinessSignal.MainWindowVisible => "main window",
            ReadinessSignal.WaitForInputIdle => "input idle",
            ReadinessSignal.ActivityQuiet => "activity quiet",
            ReadinessSignal.ServiceRunning => "service running",
            ReadinessSignal.EarlyExit => "exited early",
            ReadinessSignal.Timeout => "timed out",
            _ => m.SignalFired.ToString(),
        };
        var duration = m.TotalDuration is TimeSpan d ? FormatDuration(d) : "-";
        return $"Last launch: {duration} ({coldMark}, signal: {signalText})";
    }

    public AppEntry ToModel() => new()
    {
        Name = Name,
        Kind = Kind,
        Path = Path,
        Service = Service,
        Args = Args,
        WorkingDirectory = WorkingDirectory,
        DelayAfterSeconds = DelayAfterSeconds,
        Enabled = Enabled
    };

    public static AppEntryViewModel FromModel(AppEntry app) => new()
    {
        Name = app.Name,
        Kind = app.Kind,
        Path = app.Path,
        Service = app.Service,
        Args = app.Args,
        WorkingDirectory = app.WorkingDirectory,
        DelayAfterSeconds = app.DelayAfterSeconds,
        Enabled = app.Enabled
    };
}
