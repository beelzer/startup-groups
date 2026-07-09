using Salvo.Core.Models;
using Salvo.Core.Services;
using static Salvo.Core.Tests.OrchestratorTestHarness;

namespace Salvo.Core.Tests;

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
        using var temp = new TempDirectory();
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
        using var temp = new TempDirectory();
        var path = temp.CreateFile("fake.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out _);
        inspector.RunningByExe["fake"] = false;

        var result = orchestrator.StopApp(new AppEntry { Path = path });

        result.Status.Should().Be(OperationStatus.AlreadyInState);
    }

    [Fact]
    public void StopApp_Exe_Kills_WhenRunning()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("fake.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out _);
        inspector.RunningByExe["fake"] = true;
        inspector.KillResult = (true, "Stopped");

        var result = orchestrator.StopApp(new AppEntry { Path = path });

        result.Status.Should().Be(OperationStatus.Succeeded);
    }

    [Fact]
    public async Task LaunchGroupAsync_RunsAllApps_InSingleParallelWave()
    {
        using var temp = new TempDirectory();
        var p1 = temp.CreateFile("one.exe");
        var p2 = temp.CreateFile("two.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["one"] = false;
        inspector.RunningByExe["two"] = false;
        launcher.LaunchResult = (true, "Launched");

        // Two apps with no delay → Migrate fans both out from Start in one wave.
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

    [Fact]
    public async Task LaunchGroupAsync_HonorsWaitBarrier_BetweenWaves()
    {
        using var temp = new TempDirectory();
        var p1 = temp.CreateFile("one.exe");
        var p2 = temp.CreateFile("two.exe");

        var orchestrator = BuildOrchestrator(out _, out var inspector, out var launcher);
        inspector.RunningByExe["one"] = false;
        inspector.RunningByExe["two"] = false;

        // DelayAfterSeconds on the first app makes Migrate insert a WaitNode
        // barrier, so "two" cannot launch until "one" (and its wait) complete.
        var group = new Group
        {
            Apps =
            [
                new AppEntry { Name = "one", Path = p1, DelayAfterSeconds = 1 },
                new AppEntry { Name = "two", Path = p2 }
            ]
        };

        await orchestrator.LaunchGroupAsync(group);

        launcher.LaunchOrder.Should().Equal("one", "two");
    }

}
