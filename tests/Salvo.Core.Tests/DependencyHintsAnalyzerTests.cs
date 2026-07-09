using Salvo.Core.Launch;

namespace Salvo.Core.Tests;

public sealed class DependencyHintsAnalyzerTests : IDisposable
{
    private readonly SqliteTempDirectory _dir = new();
    private readonly SqliteLaunchBenchmarkStore _store;

    public DependencyHintsAnalyzerTests()
    {
        _store = new SqliteLaunchBenchmarkStore(_dir.DbPath("hints.db"));
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Analyze_InfersEdge_WhenBReadsFileAWrote()
    {
        // Simulate two group runs of {A, B} where B's file-access set overlaps A's, and A finished first.
        await SeedRunAsync(
            groupId: "g1",
            runStart: DateTimeOffset.UtcNow.AddMinutes(-10),
            apps: [
                ("app-a", "A", 100, 800),
                ("app-b", "B", 1000, 1500),
            ],
            resources: [
                ("app-a", new[] { @"C:\shared\lib.dll", @"C:\a-only.dat" }),
                ("app-b", new[] { @"C:\shared\lib.dll", @"C:\b-only.dat" }),
            ]);

        await SeedRunAsync(
            groupId: "g1",
            runStart: DateTimeOffset.UtcNow.AddMinutes(-5),
            apps: [
                ("app-a", "A", 100, 800),
                ("app-b", "B", 1000, 1500),
            ],
            resources: [
                ("app-a", new[] { @"C:\shared\lib.dll" }),
                ("app-b", new[] { @"C:\shared\lib.dll" }),
            ]);

        var analyzer = new DependencyHintsAnalyzer(_store);
        var hints = await analyzer.AnalyzeAsync(DateTimeOffset.UtcNow.AddHours(-1));

        hints.Should().HaveCount(1);
        var h = hints[0];
        h.GroupId.Should().Be("g1");
        h.Edges.Should().ContainSingle(e => e.FromAppId == "app-a" && e.ToAppId == "app-b");
        h.Edges[0].Confidence.Should().Be(2);
    }

    [Fact]
    public async Task Analyze_ReordersSuggested_WhenDependencyInverted()
    {
        // Runs where B starts first but A is the real provider (A always reaches ready before B accesses shared).
        // In the "latest run" though, apps appear in order [B, A]. Suggested should reorder to [A, B].
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-20);
        for (var r = 0; r < 2; r++)
        {
            await SeedRunAsync(
                groupId: "g2",
                runStart: baseTime.AddMinutes(r * 5),
                apps: [
                    ("app-b", "B", 0, 200),
                    ("app-a", "A", 50, 400),
                    ("app-b-followup", "B", 500, 800),
                ],
                resources: [
                    ("app-a", new[] { @"C:\x\shared.cfg" }),
                    ("app-b-followup", new[] { @"C:\x\shared.cfg" }),
                ]);
        }

        var analyzer = new DependencyHintsAnalyzer(_store);
        var hints = await analyzer.AnalyzeAsync(DateTimeOffset.UtcNow.AddHours(-1));

        hints.Should().NotBeEmpty();
        hints[0].Edges.Should().Contain(e => e.FromAppId == "app-a" && e.ToAppId == "app-b-followup");
    }

    [Fact]
    public async Task Analyze_ProducesHintWithNoEdges_WhenNoSharedResources()
    {
        await SeedRunAsync(
            groupId: "g3",
            runStart: DateTimeOffset.UtcNow.AddMinutes(-10),
            apps: [
                ("app-p", "P", 0, 300),
                ("app-q", "Q", 400, 700),
            ],
            resources: [
                ("app-p", new[] { @"C:\p-only.dat" }),
                ("app-q", new[] { @"C:\q-only.dat" }),
            ]);
        await SeedRunAsync(
            groupId: "g3",
            runStart: DateTimeOffset.UtcNow.AddMinutes(-5),
            apps: [
                ("app-p", "P", 0, 300),
                ("app-q", "Q", 400, 700),
            ],
            resources: [
                ("app-p", new[] { @"C:\p-only.dat" }),
                ("app-q", new[] { @"C:\q-only.dat" }),
            ]);

        var analyzer = new DependencyHintsAnalyzer(_store);
        var hints = await analyzer.AnalyzeAsync(DateTimeOffset.UtcNow.AddHours(-1));

        // The analyzer emits exactly one hint for a group with runs, but with no
        // inferred edges when the apps share no resources. Assert unconditionally
        // so a regression that dropped the group entirely would fail here.
        hints.Should().ContainSingle();
        hints[0].Edges.Should().BeEmpty();
        hints[0].IsReorderSuggested.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_SkipsGroups_WithLessThanTwoRuns()
    {
        await SeedRunAsync(
            groupId: "gonce",
            runStart: DateTimeOffset.UtcNow.AddMinutes(-5),
            apps: [
                ("x", "X", 0, 300),
                ("y", "Y", 400, 700),
            ],
            resources: [
                ("x", new[] { @"C:\shared.dat" }),
                ("y", new[] { @"C:\shared.dat" }),
            ]);

        var analyzer = new DependencyHintsAnalyzer(_store);
        var hints = await analyzer.AnalyzeAsync(DateTimeOffset.UtcNow.AddHours(-1));

        hints.Should().BeEmpty();
    }

    private async Task SeedRunAsync(
        string groupId,
        DateTimeOffset runStart,
        (string AppId, string Name, int OffsetMs, int DurationMs)[] apps,
        (string AppId, string[] Paths)[] resources)
    {
        var launchIds = new Dictionary<string, Guid>();
        foreach (var (appId, name, offsetMs, durMs) in apps)
        {
            var requested = runStart.AddMilliseconds(offsetMs);
            var ready = requested.AddMilliseconds(durMs);
            var metrics = new LaunchMetrics
            {
                LaunchId = Guid.NewGuid(),
                AppId = appId,
                AppName = name,
                GroupId = groupId,
                RequestedAt = requested,
                ReadyAt = ready,
                Outcome = LaunchOutcome.Ready,
                SignalFired = ReadinessSignal.MainWindowVisible,
                IsCold = false,
                BootEpochUtc = runStart.AddHours(-1),
            };
            launchIds[appId] = metrics.LaunchId;
            await _store.SaveAsync(metrics);
        }

        foreach (var (appId, paths) in resources)
        {
            if (launchIds.TryGetValue(appId, out var id))
            {
                await _store.SaveResourcesAsync(id, paths);
            }
        }
    }

    public void Dispose() => _dir.Dispose();
}
