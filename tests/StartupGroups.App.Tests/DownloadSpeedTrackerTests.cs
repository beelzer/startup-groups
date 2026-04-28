using StartupGroups.App.Services;

namespace StartupGroups.App.Tests;

/// <summary>
/// Covers the smoothed-speed/ETA computation shown in the Update flyout
/// during a download. Uses an injected clock for determinism — the
/// production tracker reads <c>DateTime.UtcNow</c>.
/// </summary>
public sealed class DownloadSpeedTrackerTests
{
    private DateTime _now;
    private DownloadSpeedTracker NewTracker() => new(() => _now);

    [Fact]
    public void Sample_ReturnsEmpty_WhenTotalBytesIsZero()
    {
        // Zero-byte payloads happen briefly at the start of a download
        // before Velopack reports the size. We must not divide by zero.
        var t = NewTracker();
        t.Sample(percent: 50, totalBytes: 0).Should().BeEmpty();
    }

    [Fact]
    public void Sample_ReturnsEmpty_WhenPercentIsZero()
    {
        // The first progress callback often reports 0% before any bytes
        // have transferred. Returning a real speed/ETA from one sample
        // would print "0 B/s · --" which is just noise.
        var t = NewTracker();
        t.Sample(percent: 0, totalBytes: 100_000_000).Should().BeEmpty();
    }

    [Fact]
    public void Sample_ReturnsEmpty_BeforeMinimumWindowElapses()
    {
        // The 750ms window suppresses the jitter that otherwise makes
        // the speed text twitch on every progress tick (Velopack fires
        // them every ~50ms during the hot phase).
        _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = NewTracker();
        t.Sample(percent: 10, totalBytes: 100_000_000).Should().BeEmpty();

        _now = _now.AddMilliseconds(500);
        t.Sample(percent: 20, totalBytes: 100_000_000).Should().BeEmpty();
    }

    [Fact]
    public void Sample_ReturnsFormattedSpeed_AfterWindowElapses()
    {
        // Two samples 1 second apart, 1 MiB delta → 1 MB/s.
        _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = NewTracker();
        t.Sample(percent: 10, totalBytes: 10_485_760);   // 1 MiB done

        _now = _now.AddSeconds(1);
        var result = t.Sample(percent: 20, totalBytes: 10_485_760); // 2 MiB done

        result.Should().Contain("MB/s");
    }

    [Fact]
    public void Sample_AfterReset_StartsFreshWindow()
    {
        // Reset clears the sliding window, so a follow-up sample is
        // treated as the first sample (no speed yet, returns empty).
        _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = NewTracker();
        t.Sample(percent: 10, totalBytes: 10_485_760);
        _now = _now.AddSeconds(2);
        t.Sample(percent: 20, totalBytes: 10_485_760).Should().NotBeEmpty();

        t.Reset();
        _now = _now.AddSeconds(2);
        t.Sample(percent: 25, totalBytes: 10_485_760).Should().BeEmpty();
    }

    [Fact]
    public void FormatSpeed_UsesMegabytes_AboveMebibyteThreshold()
    {
        // Anything ≥ 1 MiB/s renders as MB/s with 1 decimal — the eye
        // wants tighter resolution at the speeds users actually see.
        DownloadSpeedTracker.FormatSpeed(1_500_000).Should().Be("1.4 MB/s");
    }

    [Fact]
    public void FormatSpeed_UsesKilobytes_BetweenKibAndMib()
    {
        // KB/s for kilobyte-range speeds; rendered as integer for readability.
        DownloadSpeedTracker.FormatSpeed(50_000).Should().Be("49 KB/s");
    }

    [Fact]
    public void FormatSpeed_UsesBytes_BelowKibThreshold()
    {
        DownloadSpeedTracker.FormatSpeed(500).Should().Be("500 B/s");
    }

    [Fact]
    public void FormatEta_UsesSeconds_BelowOneMinute()
    {
        DownloadSpeedTracker.FormatEta(30).Should().Be("30s");
    }

    [Fact]
    public void FormatEta_UsesMinutes_BetweenOneMinuteAndOneHour()
    {
        DownloadSpeedTracker.FormatEta(125).Should().Be("2m");
    }

    [Fact]
    public void FormatEta_UsesHours_AboveOneHour()
    {
        DownloadSpeedTracker.FormatEta(7200).Should().Be("2.0h");
    }

    [Fact]
    public void FormatEta_ReturnsDoubleDash_ForNonFiniteOrNegative()
    {
        // Defensive: dividing by a near-zero speed during the first few
        // hundred ms can produce Infinity. The UI renders "--" rather than
        // a spinning huge number.
        DownloadSpeedTracker.FormatEta(double.PositiveInfinity).Should().Be("--");
        DownloadSpeedTracker.FormatEta(double.NaN).Should().Be("--");
        DownloadSpeedTracker.FormatEta(-1).Should().Be("--");
    }
}
