using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.Core.Tests;

/// <summary>
/// Shared orchestrator test doubles and builder, previously duplicated (and
/// drifting) across AppOrchestratorTests and GraphOrchestratorTests. FakeServices
/// is the superset: configurable Start/Stop results plus call tracking.
/// </summary>
internal static class OrchestratorTestHarness
{
    public static AppOrchestrator BuildOrchestrator(
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
}

internal sealed class FakeServices : IServiceController
{
    public Dictionary<string, ServiceState> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (bool Success, string Message)> StartResult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (bool Success, string Message)> StopResult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Started { get; } = [];
    public List<string> Stopped { get; } = [];

    public ServiceState QueryStatus(string serviceName) =>
        Status.TryGetValue(serviceName, out var state) ? state : ServiceState.NotFound;

    public bool TryStart(string serviceName, TimeSpan timeout, out string message)
    {
        Started.Add(serviceName);
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
        Stopped.Add(serviceName);
        if (StopResult.TryGetValue(serviceName, out var r))
        {
            message = r.Message;
            return r.Success;
        }
        message = "Stopped";
        return true;
    }
}

internal sealed class FakeInspector : IProcessInspector
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

internal sealed class FakeLauncher : IProcessLauncher
{
    public (bool Success, string Message) LaunchResult { get; set; } = (true, "Launched");
    public AppEntry? LastCall { get; private set; }
    public int CallCount { get; private set; }
    public List<string> LaunchOrder { get; } = [];

    public bool TryStart(AppEntry app, string resolvedPath, out string message)
    {
        CallCount++;
        LastCall = app;
        LaunchOrder.Add(app.Name);
        message = LaunchResult.Message;
        return LaunchResult.Success;
    }

    public bool TryStartAndCapture(AppEntry app, string resolvedPath, out System.Diagnostics.Process? process, out string message)
    {
        process = null;
        return TryStart(app, resolvedPath, out message);
    }
}
