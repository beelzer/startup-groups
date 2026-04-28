using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StartupGroups.App.Localization;
using StartupGroups.App.Resources;
using StartupGroups.App.Services;
using StartupGroups.Core.Branding;

namespace StartupGroups.App.ViewModels;

/// <summary>
/// Drives the <c>UpdateFlyoutWindow</c>. Owned by the main window's view-
/// model; created fresh each time the user opens the flyout so progress
/// state stays scoped to a single download.
/// </summary>
public partial class UpdateFlyoutViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;
    private readonly ILogger<UpdateFlyoutViewModel> _logger;
    private readonly DownloadSpeedTracker _speedTracker = new();
    private long _totalBytes;

    public event EventHandler? CloseRequested;

    public UpdateFlyoutViewModel(IUpdateService updateService, ILogger<UpdateFlyoutViewModel> logger)
    {
        _updateService = updateService;
        _logger = logger;
        LocalizationManager.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(HeaderSubtitle));
                OnPropertyChanged(nameof(InstallButtonLabel));
                OnPropertyChanged(nameof(DeferButtonLabel));
                OnPropertyChanged(nameof(DownloadingLabel));
            }
        };
    }

    [ObservableProperty] private string _latestVersion = string.Empty;
    [ObservableProperty] private UpdateChannel _channel = UpdateChannel.Stable;
    [ObservableProperty] private string _releaseNotesMarkdown = string.Empty;
    [ObservableProperty] private string? _releaseUrl;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private string _failureMessage = string.Empty;

    public bool CanDefer => !IsDownloading;

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanDefer));

    public string AppName => AppBranding.AppName;

    public string HeaderTitle => Strings.UpdateFlyout_Header_UpdateAvailable;

    public string HeaderSubtitle =>
        string.Format(
            CultureInfo.CurrentUICulture,
            Strings.UpdateFlyout_Header_VersionFormat,
            string.IsNullOrEmpty(LatestVersion) ? "?" : $"v{LatestVersion}",
            ChannelDisplayName);

    public string ChannelDisplayName => Channel switch
    {
        UpdateChannel.Canary => Strings.Settings_UpdateChannel_Canary,
        _ => Strings.Settings_UpdateChannel_Stable,
    };

    public string InstallButtonLabel => Strings.UpdateFlyout_Install;
    public string DeferButtonLabel => Strings.UpdateFlyout_Defer;
    public string DownloadingLabel => Strings.Settings_Version_Downloading;

    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(HeaderSubtitle));
    partial void OnChannelChanged(UpdateChannel value)
    {
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(ChannelDisplayName));
    }

    public void Initialize(UpdateCheckResult result)
    {
        LatestVersion = result.LatestVersion ?? string.Empty;
        Channel = result.Channel;
        ReleaseNotesMarkdown = result.ReleaseNotesMarkdown ?? string.Empty;
        ReleaseUrl = result.ReleaseUrl;
        _totalBytes = result.DownloadSizeBytes ?? 0L;
        ProgressText = FormatProgress(0, _totalBytes);
        SpeedText = string.Empty;
        DownloadProgress = 0;
        IsDownloading = false;
        HasFailed = false;
        FailureMessage = string.Empty;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsDownloading) return;
        if (!_updateService.CanUpdate)
        {
            // Not under a Velopack install — fall through to the browser
            // fallback handled by the parent VM.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsDownloading = true;
        HasFailed = false;
        DownloadProgress = 0;
        ProgressText = FormatProgress(0, _totalBytes);
        SpeedText = string.Empty;
        _speedTracker.Reset();
        InstallCommand.NotifyCanExecuteChanged();

        var progress = new Progress<int>(percent =>
        {
            DownloadProgress = percent / 100.0;
            ProgressText = FormatProgress(percent, _totalBytes);
            SpeedText = _speedTracker.Sample(percent, _totalBytes);
        });

        try
        {
            // Returns only on failure — apply restarts the process.
            await _updateService.DownloadAndApplyAsync(progress).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download/apply failed.");
            HasFailed = true;
            FailureMessage = ex.Message;
            IsDownloading = false;
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Defer()
    {
        if (IsDownloading) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = string.IsNullOrEmpty(ReleaseUrl)
            ? $"{AppBranding.SupportUrl}/releases/latest"
            : ReleaseUrl;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open release notes URL");
        }
    }

    private static string FormatProgress(int percent, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return $"{percent}%";
        }
        const double Mib = 1024d * 1024d;
        var totalMib = totalBytes / Mib;
        var doneMib = totalMib * percent / 100d;
        return $"{doneMib:F1} / {totalMib:F1} MB ({percent}%)";
    }
}

/// <summary>
/// Computes a smoothed download speed and ETA from a sequence of
/// (percent, total-bytes) samples. Uses a 5-sample sliding window over a
/// minimum of 750ms so the UI doesn't twitch on small fluctuations.
/// </summary>
internal sealed class DownloadSpeedTracker
{
    private const int WindowSize = 5;
    private static readonly TimeSpan MinSpan = TimeSpan.FromMilliseconds(750);

    private readonly Queue<(DateTime At, long Bytes)> _samples = new();

    public void Reset() => _samples.Clear();

    public string Sample(int percent, long totalBytes)
    {
        if (totalBytes <= 0 || percent <= 0) return string.Empty;

        var now = DateTime.UtcNow;
        var done = (long)(totalBytes * (percent / 100.0));
        _samples.Enqueue((now, done));
        while (_samples.Count > WindowSize) _samples.Dequeue();

        var first = _samples.Peek();
        var span = now - first.At;
        if (span < MinSpan || _samples.Count < 2) return string.Empty;

        var bytesDelta = done - first.Bytes;
        if (bytesDelta <= 0) return string.Empty;

        var bytesPerSec = bytesDelta / span.TotalSeconds;
        var remaining = Math.Max(0, totalBytes - done);
        var etaSeconds = remaining / bytesPerSec;

        return string.Format(
            CultureInfo.CurrentUICulture,
            Strings.UpdateFlyout_SpeedEtaFormat,
            FormatSpeed(bytesPerSec),
            FormatEta(etaSeconds));
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        const double Kib = 1024d;
        const double Mib = 1024d * 1024d;
        if (bytesPerSec >= Mib) return $"{bytesPerSec / Mib:F1} MB/s";
        if (bytesPerSec >= Kib) return $"{bytesPerSec / Kib:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    private static string FormatEta(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds <= 0) return "--";
        if (seconds < 60) return $"{seconds:F0}s";
        var minutes = seconds / 60.0;
        if (minutes < 60) return $"{minutes:F0}m";
        return $"{minutes / 60:F1}h";
    }
}
