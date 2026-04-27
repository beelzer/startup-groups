using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Tests;

public sealed class AppOrchestratorTests
{
    [Fact]
    public void LaunchApp_Service_ReturnsNotFound_WhenServiceMissing()
    {
        var orchestrator = BuildOrchestrator(out var services, out _, out _);
        services.Status[""] = ServiceState.NotFound;
        services.Status["Ghost"] = ServiceState.NotFound;

        var result = orchestrator.LaunchApp(new AppEntry { Kind = AppKind.Service, Service = "Ghost" });

        result.Status.Should().Be(OperationStatus.NotFound);
    }

    [Fact]
    public void LaunchApp_Service_ReturnsAlreadyInState_WhenAlreadyRunning()
    {
        var orchestrator = BuildOrchestrator(out var services, out _, out _);
        services.Status["Radarr"] = ServiceState.Running;

        var result = orchestrator.LaunchApp(new AppEntry { Kind = AppKind.Service, Service = "Radarr" });

        result.Status.Should().Be(OperationStatus.AlreadyInState);
    }

    [Fact]
    public void LaunchApp_Service_SurfacesNeedsElevation_OnAccessDenied()
    {
        var orchestrator = BuildOrchestrator(out var services, out _, out _);
        services.Status["Radarr"] = ServiceState.Stopped;
        services.StartResult["Radarr"] = (false, "Needs admin");

        var result = orchestrator.LaunchApp(new AppEntry { Kind = AppKind.Service, Service = "Radarr" });

        result.Status.Should().Be(OperationStatus.NeedsElevation);
    }

    [Fact]
    public void LaunchApp_Exe_NotFound_WhenUnresolvable()
    {
        var orchestrator = BuildOrchestrator(out _, out _, out _);

        var result = orchestrator.LaunchApp(new AppEntry { Path = @"C:\does\not\exist.exe" });

        result.Status.Should().Be(OperationStatus.NotFound);
    }

    [Fact]
    public void LaunchApp_Exe_Launches_WhenResolved()
    {
        using var temp = new TempDir();
        var path = temp.CreateFile("fake.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["fake"] = false;
        launcher.LaunchResult = (true, "Launched");

        var result = orchestrator.LaunchApp(new AppEntry { Path = path });

        result.Status.Should().Be(OperationStatus.Succeeded);
        launcher.LastCall.Should().NotBeNull();
    }

    [Fact]
    public void StopApp_Exe_ReturnsAlreadyInState_WhenNotRunning()
    {
        using var temp = new TempDir();
        var path = temp.CreateFile("fake.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out _);
        inspector.RunningByExe["fake"] = false;

        var result = orchestrator.StopApp(new AppEntry { Path = path });

        result.Status.Should().Be(OperationStatus.AlreadyInState);
    }

    [Fact]
    public void StopApp_Exe_Kills_WhenRunning()
    {
        using var temp = new TempDir();
        var path = temp.CreateFile("fake.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out _);
        inspector.RunningByExe["fake"] = true;
        inspector.KillResult = (true, "Stopped");

        var result = orchestrator.StopApp(new AppEntry { Path = path });

        result.Status.Should().Be(OperationStatus.Succeeded);
    }

    [Fact]
    public async Task LaunchGroupAsync_RunsEachApp_InOrder()
    {
        using var temp = new TempDir();
        var p1 = temp.CreateFile("one.exe");
        var p2 = temp.CreateFile("two.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["one"] = false;
        inspector.RunningByExe["two"] = false;
        launcher.LaunchResult = (true, "Launched");

        var group = new Group
        {
            Apps =
            [
                new AppEntry { Name = "one", Path = p1 },
                new AppEntry { Name = "two", Path = p2 }
            ]
        };

        var results = await orchestrator.LaunchGroupAsync(group);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Status == OperationStatus.Succeeded);
        launcher.CallCount.Should().Be(2);
    }

    private static AppOrchestrator BuildOrchestrator(
        out FakeServices services,
        out FakeInspector inspector,
        out FakeLauncher launcher)
    {
        services = new FakeServices();
        inspector = new FakeInspector();
        launcher = new FakeLauncher();
        var resolver = new PathResolver();
        var matchers = new ProcessMatcherResolver(resolver);
        return new AppOrchestrator(resolver, launcher, inspector, matchers, services);
    }

    private sealed class FakeServices : IServiceController
    {
        public Dictionary<string, ServiceState> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (bool Success, string Message)> StartResult { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (bool Success, string Message)> StopResult { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ServiceState QueryStatus(string serviceName) =>
            Status.TryGetValue(serviceName, out var state) ? state : ServiceState.NotFound;

        public bool TryStart(string serviceName, TimeSpan timeout, out string message)
        {
            if (StartResult.TryGetValue(serviceName, out var r))
            {
                message = r.Message;
                return r.Success;
            }

            message = "Started";
            return true;
        }

        public bool TryStop(string serviceName, TimeSpan timeout, out string message)
        {
            if (StopResult.TryGetValue(serviceName, out var r))
            {
                message = r.Message;
                return r.Success;
            }

            message = "Stopped";
            return true;
        }
    }

    private sealed class FakeInspector : IProcessInspector
    {
        public Dictionary<string, bool> RunningByExe { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (bool Success, string Message) KillResult { get; set; } = (true, "Stopped");

        public bool IsRunning(IReadOnlyList<ProcessMatcher> matchers)
        {
            foreach (var m in matchers)
            {
                if (!string.IsNullOrEmpty(m.ExeName) && RunningByExe.TryGetValue(m.ExeName!, out var v) && v)
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryKill(IReadOnlyList<ProcessMatcher> matchers, out string message)
        {
            message = KillResult.Message;
            return KillResult.Success;
        }

        public IReadOnlyList<int> FindMatchingPids(IReadOnlyList<ProcessMatcher> matchers) =>
            Array.Empty<int>();
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public (bool Success, string Message) LaunchResult { get; set; } = (true, "Launched");
        public AppEntry? LastCall { get; private set; }
        public int CallCount { get; private set; }

        public bool TryStart(AppEntry app, string resolvedPath, out string message)
        {
            CallCount++;
            LastCall = app;
            message = LaunchResult.Message;
            return LaunchResult.Success;
        }

        public bool TryStartAndCapture(AppEntry app, string resolvedPath, out System.Diagnostics.Process? process, out string message)
        {
            process = null;
            return TryStart(app, resolvedPath, out message);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sg-orch-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public string CreateFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "");
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
