using System.Diagnostics;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Native;

namespace StartupGroups.Core.Tests;

public sealed class LaunchSessionTests
{
    [Fact]
    public void Begin_InitializesIdsAndTimestamp()
    {
        using var session = LaunchSession.Begin();
        session.LaunchId.Should().NotBe(Guid.Empty);
        session.RequestedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        session.ReadyAt.Should().BeNull();
        session.RootPid.Should().BeNull();
    }

    [Fact]
    public void TryMarkReady_IsFirstWins_AndRecordsSignal()
    {
        using var session = LaunchSession.Begin();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);

        session.TryMarkReady(t1, ReadinessSignal.MainWindowVisible).Should().BeTrue();
        session.TryMarkReady(t2, ReadinessSignal.ActivityQuiet).Should().BeFalse();

        session.ReadyAt.Should().Be(t1);
        session.SignalFired.Should().Be(ReadinessSignal.MainWindowVisible);
    }

    [Fact]
    public void TryMarkPhases_AreFirstWins()
    {
        using var session = LaunchSession.Begin();
        var t1 = DateTimeOffset.UtcNow;

        session.TryMarkMainWindow(t1).Should().BeTrue();
        session.TryMarkMainWindow(t1.AddSeconds(1)).Should().BeFalse();
        session.MainWindowAt.Should().Be(t1);

        session.TryMarkInputIdle(t1).Should().BeTrue();
        session.TryMarkInputIdle(t1.AddSeconds(1)).Should().BeFalse();

        session.TryMarkQuiet(t1).Should().BeTrue();
        session.TryMarkQuiet(t1.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void RecordPidResolved_SetsRootWhenAbsent_AndPreservesWhenPresent()
    {
        using var session = LaunchSession.Begin();
        session.RecordPidResolved(1234);
        session.RootPid.Should().Be(1234);

        session.RecordPidResolved(9999);
        session.RootPid.Should().Be(1234);
    }

    [Fact]
    public void AttachRootProcess_CurrentProcess_RecordsPid()
    {
        using var session = LaunchSession.Begin();
        using var current = Process.GetCurrentProcess();

        session.AttachRootProcess(current);

        session.RootPid.Should().Be(current.Id);
        session.ProcessStartReturnedAt.Should().NotBeNull();
        session.PidResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void IsTreeAlive_TrueForCurrentProcess()
    {
        using var session = LaunchSession.Begin();
        using var current = Process.GetCurrentProcess();
        session.AttachRootProcess(current);

        session.IsTreeAlive().Should().BeTrue();
    }

    [Fact]
    public void EnumerateDescendantPids_AtLeastReturnsRootPid()
    {
        using var session = LaunchSession.Begin();
        using var current = Process.GetCurrentProcess();
        session.AttachRootProcess(current);

        var pids = session.EnumerateDescendantPids();
        pids.Should().NotBeEmpty();
        pids.Should().Contain(current.Id);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var session = LaunchSession.Begin();
        session.Dispose();
        session.Dispose();
    }
}

public sealed class ProcessTreeSnapshotTests
{
    [Fact]
    public void CaptureAll_ReturnsCurrentProcess()
    {
        var entries = ProcessTreeSnapshot.CaptureAll();
        var myPid = Process.GetCurrentProcess().Id;
        entries.Should().Contain(e => e.Pid == myPid);
    }

    [Fact]
    public void GetDescendantPids_ForNonExistentRoot_ReturnsEmpty()
    {
        var pids = ProcessTreeSnapshot.GetDescendantPids(0);
        pids.Should().BeEmpty();
    }

    [Fact]
    public void GetDescendantPids_CurrentProcess_ReturnsNoRoot()
    {
        var myPid = Process.GetCurrentProcess().Id;
        var pids = ProcessTreeSnapshot.GetDescendantPids(myPid);
        pids.Should().NotContain(myPid);
    }
}
