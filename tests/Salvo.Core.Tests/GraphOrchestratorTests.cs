using Salvo.Core.Models;
using Salvo.Core.Models.Flow;
using Salvo.Core.Services;
using static Salvo.Core.Tests.OrchestratorTestHarness;

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
        using var temp = new TempDirectory();
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
        using var temp = new TempDirectory();
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
        using var temp = new TempDirectory();
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
    public async Task ExecuteGraph_GroupCall_SequentialRepeatCall_RunsTargetTwice()
    {
        using var temp = new TempDirectory();
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
        // Start → Call(sub) → Call(sub): the second call is sequential, not
        // recursive — the chain has already exited "sub" by then. Before the
        // fix, callChain never removed entries and dropped it as "recursive".
        var boot = new Group
        {
            Id = "boot",
            Nodes =
            [
                new StartNode { Id = "bs" },
                new GroupCallNode { Id = "c1", GroupId = "sub" },
                new GroupCallNode { Id = "c2", GroupId = "sub" },
            ],
            Edges =
            [
                new Edge { Id = "e1", From = "bs", To = "c1" },
                new Edge { Id = "e2", From = "c1", To = "c2" },
            ],
        };
        orchestrator.GroupResolver = id => id == "sub" ? sub : null;

        var results = await orchestrator.LaunchGroupAsync(boot);

        launcher.CallCount.Should().Be(2);
        results.Should().HaveCount(2);
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
    public async Task ExecuteGraph_IfElse_FiresElseBranch_WhenConditionFalse()
    {
        using var temp = new TempDirectory();
        var pThen = temp.CreateFile("then.exe");
        var pElse = temp.CreateFile("else.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["then"] = false;
        inspector.RunningByExe["else"] = false;
        launcher.LaunchResult = (true, "Launched");

        // Condition points at a file that does NOT exist → else branch taken.
        var start = new StartNode { Id = "s" };
        var ifElse = new IfElseNode
        {
            Id = "if",
            Condition = new FileExistsCondition { Path = Path.Combine(temp.Root, "missing.flag") },
        };
        var then = new AppNode { Id = "t", App = new AppEntry { Name = "then", Path = pThen } };
        var els = new AppNode { Id = "e", App = new AppEntry { Name = "else", Path = pElse } };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, ifElse, then, els],
            Edges =
            [
                new Edge { Id = "e1", From = "s", To = "if" },
                new Edge { Id = "e2", From = "if", To = "t", Label = "then" },
                new Edge { Id = "e3", From = "if", To = "e", Label = "else" },
            ],
        };

        var results = await orchestrator.LaunchGroupAsync(group);

        launcher.CallCount.Should().Be(1);
        results.Select(r => r.Source?.Name).Should().Contain("else");
        results.Select(r => r.Source?.Name).Should().NotContain("then");
    }

    [Fact]
    public async Task ExecuteGraph_ServiceStartAndStop_InvokeTheController()
    {
        var orchestrator = BuildOrchestrator(out var services, out _, out _);
        services.Status["Svc"] = ServiceState.Stopped;

        var start = new StartNode { Id = "s" };
        var svcStart = new ServiceStartNode { Id = "start", ServiceName = "Svc" };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, svcStart],
            Edges = [new Edge { Id = "e1", From = "s", To = "start" }],
        };

        await orchestrator.LaunchGroupAsync(group);
        services.Started.Should().Contain("Svc");

        // Now a stop node against a running service.
        services.Status["Svc"] = ServiceState.Running;
        var stopStart = new StartNode { Id = "s2" };
        var svcStop = new ServiceStopNode { Id = "stop", ServiceName = "Svc" };
        var stopGroup = new Group
        {
            Id = "g2",
            Nodes = [stopStart, svcStop],
            Edges = [new Edge { Id = "e2", From = "s2", To = "stop" }],
        };

        await orchestrator.LaunchGroupAsync(stopGroup);
        services.Stopped.Should().Contain("Svc");
    }

    [Fact]
    public async Task ExecuteGraph_RunCommand_MapsExitCodeToResult()
    {
        var orchestrator = BuildOrchestrator(out _, out _, out _);

        var start = new StartNode { Id = "s" };
        var ok = new RunCommandNode { Id = "ok", Command = "exit 0", Interpreter = "shell" };
        var fail = new RunCommandNode { Id = "fail", Command = "exit 3", Interpreter = "shell" };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, ok, fail],
            Edges =
            [
                new Edge { Id = "e1", From = "s", To = "ok" },
                new Edge { Id = "e2", From = "s", To = "fail" },
            ],
        };

        var results = await orchestrator.LaunchGroupAsync(group);

        results.Should().Contain(r => r.Message == "Exit 0" && r.Status == OperationStatus.Succeeded);
        results.Should().Contain(r => r.Message == "Exit 3" && r.Status == OperationStatus.Failed);
    }

    [Fact]
    public async Task LaunchGroupAsync_PreCancelledToken_ThrowsAndLaunchesNothing()
    {
        using var temp = new TempDirectory();
        var p1 = temp.CreateFile("one.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["one"] = false;

        var start = new StartNode { Id = "s" };
        var appOne = new AppNode { Id = "a1", App = new AppEntry { Name = "one", Path = p1 } };
        var group = new Group
        {
            Id = "g",
            Nodes = [start, appOne],
            Edges = [new Edge { Id = "e1", From = "s", To = "a1" }],
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await orchestrator.LaunchGroupAsync(group, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        launcher.CallCount.Should().Be(0);
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

}
