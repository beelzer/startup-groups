using System;
using System.Collections.Generic;
using System.Globalization;
using StartupGroups.App.Resources;

namespace StartupGroups.App.Services;

/// <summary>
/// Computes a smoothed download speed and ETA from a sequence of
/// (percent, total-bytes) samples. Uses a 5-sample sliding window over a
/// minimum of 750ms so the UI doesn't twitch on small fluctuations.
/// </summary>
public sealed class DownloadSpeedTracker
{
    private const int WindowSize = 5;
    private static readonly TimeSpan MinSpan = TimeSpan.FromMilliseconds(750);

    private readonly Queue<(DateTime At, long Bytes)> _samples = new();
    private readonly Func<DateTime> _clock;

    public DownloadSpeedTracker() : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>Test seam — pass a deterministic clock from unit tests.</summary>
    public DownloadSpeedTracker(Func<DateTime> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void Reset() => _samples.Clear();

    public string Sample(int percent, long totalBytes)
    {
        if (totalBytes <= 0 || percent <= 0) return string.Empty;

        var now = _clock();
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

    public static string FormatSpeed(double bytesPerSec)
    {
        const double Kib = 1024d;
        const double Mib = 1024d * 1024d;
        if (bytesPerSec >= Mib) return $"{bytesPerSec / Mib:F1} MB/s";
        if (bytesPerSec >= Kib) return $"{bytesPerSec / Kib:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    public static string FormatEta(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds <= 0) return "--";
        if (seconds < 60) return $"{seconds:F0}s";
        var minutes = seconds / 60.0;
        if (minutes < 60) return $"{minutes:F0}m";
        return $"{minutes / 60:F1}h";
    }
}
