using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Tests;

public sealed class LaunchTelemetryServicePidResolverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteLaunchBenchmarkStore _store;

    public LaunchTelemetryServicePidResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sg-pidres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _store = new SqliteLaunchBenchmarkStore(Path.Combine(_tempRoot, "t.db"));
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task BeginObservation_WithNullProcess_UsesInspectorToResolvePid()
    {
        var matchers = new MemoryMatcherResolver(new[] { new ProcessMatcher { ExeName = "fakeapp" } });
        var inspector = new StubInspector(pidSequence: new[] { Array.Empty<int>(), Array.Empty<int>(), new[] { 4321 } });

        var probe = new ImmediateReadyProbe();
        var detector = new ReadinessDetector(new IReadinessProbe[] { probe }, logger: NullLogger<ReadinessDetector>.Instance);

        var telemetry = new LaunchTelemetryService(
            detector,
            _store,
            inspector,
            matchers,
            etw: null,
            logger: NullLogger<LaunchTelemetryService>.Instance,
            timeout: TimeSpan.FromSeconds(3));

        var app = new AppEntry { Name = "FakeApp", Path = @"shell:AppsFolder\\X!App" };
        var metrics = await telemetry.BeginObservation(app, resolvedPath: null, groupId: "g", process: null);

        metrics.Outcome.Should().Be(LaunchOutcome.Ready);
        metrics.RootPid.Should().Be(4321);
        inspector.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BeginObservation_WithNullProcess_NoInspector_StillObserves()
    {
        var probe = new ImmediateReadyProbe();
        var detector = new ReadinessDetector(new IReadinessProbe[] { probe }, logger: NullLogger<ReadinessDetector>.Instance);

        var telemetry = new LaunchTelemetryService(
            detector,
            _store,
            inspector: null,
            matchers: null,
            etw: null,
            logger: NullLogger<LaunchTelemetryService>.Instance,
            timeout: TimeSpan.FromSeconds(2));

        var app = new AppEntry { Name = "ShellLauncher", Path = @"shell:AppsFolder\\X!App" };
        var metrics = await telemetry.BeginObservation(app, resolvedPath: null, groupId: null, process: null);

        metrics.RootPid.Should().BeNull();
        metrics.Outcome.Should().BeOneOf(
            LaunchOutcome.Ready,
            LaunchOutcome.TimedOut,
            LaunchOutcome.ExitedEarly,
            LaunchOutcome.Unknown);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private sealed class MemoryMatcherResolver : IProcessMatcherResolver
    {
        private readonly IReadOnlyList<ProcessMatcher> _matchers;
        public MemoryMatcherResolver(IReadOnlyList<ProcessMatcher> matchers) { _matchers = matchers; }
        public IReadOnlyList<ProcessMatcher> GetMatchers(AppEntry app) => _matchers;
    }

    private sealed class StubInspector : IProcessInspector
    {
        private readonly Queue<IReadOnlyList<int>> _returns;
        public int CallCount { get; private set; }

        public StubInspector(IEnumerable<IReadOnlyList<int>> pidSequence)
        {
            _returns = new Queue<IReadOnlyList<int>>(pidSequence);
        }

        public bool IsRunning(IReadOnlyList<ProcessMatcher> matchers) => false;
        public bool TryKill(IReadOnlyList<ProcessMatcher> matchers, out string message)
        {
            message = "noop";
            return true;
        }

        public IReadOnlyList<int> FindMatchingPids(IReadOnlyList<ProcessMatcher> matchers)
        {
            CallCount++;
            return _returns.Count > 0 ? _returns.Dequeue() : Array.Empty<int>();
        }
    }

    private sealed class ImmediateReadyProbe : IReadinessProbe
    {
        public ReadinessSignal Signal => ReadinessSignal.MainWindowVisible;
        public bool AppliesTo(ProbeContext context) => true;
        public async Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (context.Session.RootPid is not null)
                {
                    return true;
                }
                try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
                catch { return false; }
            }
            return false;
        }
    }
}
