using Salvo.Core.Models;
using Salvo.Core.Models.Flow;
using Salvo.Core.Services;

namespace Salvo.Core.Tests;

/// <summary>
/// Locks the new code paths added in Phase A2: action nodes
/// (ServiceStart/ServiceStop/RunCommand), IfElse skip propagation
/// through to downstream merges, and GroupCall recursion.
/// </summary>
public sealed class GraphOrchestratorTests
{
    [Fact]
    public async Task ExecuteGraph_FiresAppsInParallel_FromForkedStart()
    {
        using var temp = new TempDir();
        var p1 = temp.CreateFile("one.exe");
        var p2 = temp.CreateFile("two.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["one"] = false;
        inspector.RunningByExe["two"] = false;
        launcher.LaunchResult = (true, "Launched");

        // Start ─┬─ AppOne
        //        └─ AppTwo  (no Wait → both fan out from Start in parallel)
        var start = new StartNode { Id = "s" };
        var appOne = new AppNode { Id = "a1", App = new AppEntry { Name = "one", Path = p1 } };
        var appTwo = new AppNode { Id = "a2", App = new AppEntry { Name = "two", Path = p2 } };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, appOne, appTwo],
            Edges =
            [
                new Edge { Id = "e1", From = "s", To = "a1" },
                new Edge { Id = "e2", From = "s", To = "a2" },
            ],
        };

        var results = await orchestrator.LaunchGroupAsync(group);

        results.Should().HaveCount(2);
        launcher.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteGraph_IfElse_FiresThenBranch_AndDownstreamMergeRuns()
    {
        using var temp = new TempDir();
        var pThen = temp.CreateFile("then.exe");
        var pElse = temp.CreateFile("else.exe");
        var pAfter = temp.CreateFile("after.exe");
        var existingFile = temp.CreateFile("flag.txt");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["then"] = false;
        inspector.RunningByExe["else"] = false;
        inspector.RunningByExe["after"] = false;
        launcher.LaunchResult = (true, "Launched");

        // Start → If(FileExists=flag.txt) ─then→ AppThen ─┐
        //                                  ─else→ AppElse ─┴→ AppAfter (merge)
        var start = new StartNode { Id = "s" };
        var ifElse = new IfElseNode
        {
            Id = "if",
            Condition = new FileExistsCondition { Path = existingFile },
        };
        var then = new AppNode { Id = "t", App = new AppEntry { Name = "then", Path = pThen } };
        var els = new AppNode { Id = "e", App = new AppEntry { Name = "else", Path = pElse } };
        var after = new AppNode { Id = "a", App = new AppEntry { Name = "after", Path = pAfter } };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, ifElse, then, els, after],
            Edges =
            [
                new Edge { Id = "e1", From = "s", To = "if" },
                new Edge { Id = "e2", From = "if", To = "t", Label = "then" },
                new Edge { Id = "e3", From = "if", To = "e", Label = "else" },
                new Edge { Id = "e4", From = "t", To = "a" },
                new Edge { Id = "e5", From = "e", To = "a" },
            ],
        };

        var results = await orchestrator.LaunchGroupAsync(group);

        // Then branch + downstream merge ran (2 launches); else branch skipped.
        launcher.CallCount.Should().Be(2);
        results.Select(r => r.Source?.Name).Should().Contain(new[] { "then", "after" });
        results.Select(r => r.Source?.Name).Should().NotContain("else");
    }

    [Fact]
    public async Task ExecuteGraph_GroupCall_ResolvesAndRecursesIntoTargetGroup()
    {
        using var temp = new TempDir();
        var pSub = temp.CreateFile("sub.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["sub"] = false;
        launcher.LaunchResult = (true, "Launched");

        var sub = new Group
        {
            Id = "sub",
            Nodes =
            [
                new StartNode { Id = "ss" },
                new AppNode { Id = "sa", App = new AppEntry { Name = "sub", Path = pSub } },
            ],
            Edges = [new Edge { Id = "se", From = "ss", To = "sa" }],
        };
        var boot = new Group
        {
            Id = "boot",
            Nodes =
            [
                new StartNode { Id = "bs" },
                new GroupCallNode { Id = "bc", GroupId = "sub" },
            ],
            Edges = [new Edge { Id = "be", From = "bs", To = "bc" }],
        };
        orchestrator.GroupResolver = id => id == "sub" ? sub : null;

        var results = await orchestrator.LaunchGroupAsync(boot);

        launcher.CallCount.Should().Be(1);
        results.Should().HaveCount(1);
        results[0].Source?.Name.Should().Be("sub");
    }

    [Fact]
    public async Task ExecuteGraph_GroupCall_DetectsRecursion_AndRefusesToReenter()
    {
        var orchestrator = BuildOrchestrator(out _, out var _, out var launcher);

        // boot calls itself → orchestrator should refuse and return cleanly.
        var boot = new Group
        {
            Id = "boot",
            Nodes =
            [
                new StartNode { Id = "bs" },
                new GroupCallNode { Id = "bc", GroupId = "boot" },
            ],
            Edges = [new Edge { Id = "be", From = "bs", To = "bc" }],
        };
        orchestrator.GroupResolver = id => id == "boot" ? boot : null;

        // Shouldn't throw, shouldn't hang.
        var results = await orchestrator.LaunchGroupAsync(boot);

        launcher.CallCount.Should().Be(0);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteGraph_RunCommand_UnwindsPromptly_OnCancellation()
    {
        // Before the fix, RunCommand did a blocking WaitForExit(60_000) and only
        // checked the token afterwards, so a StopGroup/cancel was unresponsive
        // for up to a minute per running command. It must now kill the process
        // and propagate cancellation promptly.
        var orchestrator = BuildOrchestrator(out _, out _, out _);

        var start = new StartNode { Id = "s" };
        // ping -n 60 sleeps ~59s headlessly; the cancel must interrupt it.
        var cmd = new RunCommandNode { Id = "c", Command = "ping 127.0.0.1 -n 60", Interpreter = "shell" };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, cmd],
            Edges = [new Edge { Id = "e1", From = "s", To = "c" }],
        };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = async () => await orchestrator.LaunchGroupAsync(group, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        // Well under the 60s RunCommand timeout — proves the wait is cancel-aware.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
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

    // Reuse the fakes from AppOrchestratorTests — same shape, kept here
    // as nested types to avoid coupling.
    private sealed class FakeServices : IServiceController
    {
        public Dictionary<string, ServiceState> States { get; } = new();
        public ServiceState QueryStatus(string serviceName) =>
            States.TryGetValue(serviceName, out var s) ? s : ServiceState.NotFound;
        public bool TryStart(string serviceName, TimeSpan timeout, out string message)
        {
            message = "Started";
            return true;
        }
        public bool TryStop(string serviceName, TimeSpan timeout, out string message)
        {
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
        public int CallCount { get; private set; }
        public bool TryStart(AppEntry app, string resolvedPath, out string message)
        {
            CallCount++;
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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sg-graph-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Root);
        public string CreateFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "");
            return path;
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
