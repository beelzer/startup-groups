using StartupGroups.Core.Launch;

namespace StartupGroups.Core.Tests;

public sealed class SqliteLaunchBenchmarkStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;

    public SqliteLaunchBenchmarkStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sg-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Combine(_tempRoot, "bench.db");
    }

    [Fact]
    public async Task InitializeAsync_CreatesDatabaseFile()
    {
        var store = new SqliteLaunchBenchmarkStore(_dbPath);

        await store.InitializeAsync();

        File.Exists(_dbPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ThenGetRecentAsync_RoundTripsAllFields()
    {
        var store = new SqliteLaunchBenchmarkStore(_dbPath);
        await store.InitializeAsync();

        var requested = DateTimeOffset.UtcNow.AddSeconds(-5);
        var ready = requested.AddMilliseconds(1234);
        var metrics = new LaunchMetrics
        {
            LaunchId = Guid.NewGuid(),
            AppId = "path:abc123",
            AppName = "Chrome",
            GroupId = "work",
            ResolvedPath = @"C:\Program Files\Google\Chrome\chrome.exe",
            ResolvedPathHash = "abc123",
            RootPid = 4242,
            RequestedAt = requested,
            ProcessStartReturnedAt = requested.AddMilliseconds(12),
            PidResolvedAt = requested.AddMilliseconds(14),
            MainWindowAt = requested.AddMilliseconds(900),
            InputIdleAt = requested.AddMilliseconds(1100),
            QuietAt = requested.AddMilliseconds(1234),
            ReadyAt = ready,
            Outcome = LaunchOutcome.Ready,
            SignalFired = ReadinessSignal.MainWindowVisible,
            IsCold = true,
            BootEpochUtc = requested.AddHours(-2),
            AppVersion = "120.0.0",
            Notes = "observe-only",
        };

        await store.SaveAsync(metrics);

        var recent = await store.GetRecentAsync("path:abc123", 10);
        recent.Should().HaveCount(1);
        var read = recent[0];
        read.LaunchId.Should().Be(metrics.LaunchId);
        read.AppName.Should().Be("Chrome");
        read.GroupId.Should().Be("work");
        read.ResolvedPath.Should().Be(metrics.ResolvedPath);
        read.RootPid.Should().Be(4242);
        read.Outcome.Should().Be(LaunchOutcome.Ready);
        read.SignalFired.Should().Be(ReadinessSignal.MainWindowVisible);
        read.IsCold.Should().BeTrue();
        read.ReadyAt.Should().Be(ready);
        read.TotalDuration.Should().Be(TimeSpan.FromMilliseconds(1234));
    }

    [Fact]
    public async Task SaveAsync_HandlesNullableFields()
    {
        var store = new SqliteLaunchBenchmarkStore(_dbPath);
        await store.InitializeAsync();

        var metrics = new LaunchMetrics
        {
            LaunchId = Guid.NewGuid(),
            AppId = "path:xyz",
            RequestedAt = DateTimeOffset.UtcNow,
            Outcome = LaunchOutcome.Failed,
            SignalFired = ReadinessSignal.None,
            IsCold = false,
            BootEpochUtc = BootSession.BootEpochUtc,
        };

        await store.SaveAsync(metrics);

        var recent = await store.GetRecentAsync("path:xyz", 1);
        recent.Should().HaveCount(1);
        recent[0].AppName.Should().BeNull();
        recent[0].ResolvedPath.Should().BeNull();
        recent[0].ReadyAt.Should().BeNull();
        recent[0].TotalDuration.Should().BeNull();
    }

    [Fact]
    public async Task HasReadyLaunchSinceBootAsync_ReturnsTrueOnlyForReadyOutcomeAfterBoot()
    {
        var store = new SqliteLaunchBenchmarkStore(_dbPath);
        await store.InitializeAsync();

        var boot = DateTimeOffset.UtcNow.AddHours(-1);
        await store.SaveAsync(MakeMetrics("app-a", boot.AddMinutes(-30), LaunchOutcome.Ready));
        await store.SaveAsync(MakeMetrics("app-b", boot.AddMinutes(10), LaunchOutcome.TimedOut));
        await store.SaveAsync(MakeMetrics("app-c", boot.AddMinutes(20), LaunchOutcome.Ready));

        (await store.HasReadyLaunchSinceBootAsync("app-a", boot)).Should().BeFalse();
        (await store.HasReadyLaunchSinceBootAsync("app-b", boot)).Should().BeFalse();
        (await store.HasReadyLaunchSinceBootAsync("app-c", boot)).Should().BeTrue();
        (await store.HasReadyLaunchSinceBootAsync("unknown", boot)).Should().BeFalse();
    }

    [Fact]
    public async Task GetRecentAsync_RespectsLimitAndOrdersByRequestedDesc()
    {
        var store = new SqliteLaunchBenchmarkStore(_dbPath);
        await store.InitializeAsync();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(MakeMetrics("app-x", baseTime.AddMinutes(i), LaunchOutcome.Ready));
        }

        var recent = await store.GetRecentAsync("app-x", 3);
        recent.Should().HaveCount(3);
        recent[0].RequestedAt.Should().BeAfter(recent[1].RequestedAt);
        recent[1].RequestedAt.Should().BeAfter(recent[2].RequestedAt);
    }

    [Fact]
    public void BootSession_ReportsPastEpoch()
    {
        BootSession.BootEpochUtc.Should().BeBefore(DateTimeOffset.UtcNow);
        BootSession.Uptime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void AppIdentity_SamePathProducesSameId()
    {
        var a = AppIdentity.ComputeAppId(@"C:\Apps\Foo.exe", null);
        var b = AppIdentity.ComputeAppId(@"c:\apps\FOO.exe", null);
        a.Should().Be(b);
    }

    [Fact]
    public void AppIdentity_DifferentPathsProduceDifferentIds()
    {
        var a = AppIdentity.ComputeAppId(@"C:\Apps\Foo.exe", null);
        var b = AppIdentity.ComputeAppId(@"C:\Apps\Bar.exe", null);
        a.Should().NotBe(b);
    }

    private static LaunchMetrics MakeMetrics(string appId, DateTimeOffset requestedAt, LaunchOutcome outcome) => new()
    {
        LaunchId = Guid.NewGuid(),
        AppId = appId,
        RequestedAt = requestedAt,
        Outcome = outcome,
        SignalFired = outcome == LaunchOutcome.Ready ? ReadinessSignal.MainWindowVisible : ReadinessSignal.Timeout,
        IsCold = false,
        BootEpochUtc = requestedAt.AddMinutes(-5),
    };

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
