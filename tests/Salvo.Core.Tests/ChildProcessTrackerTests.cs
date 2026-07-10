using System.Diagnostics;
using Salvo.Core.Native;

namespace Salvo.Core.Tests;

public sealed class ChildProcessTrackerTests
{
    /// <summary>
    /// Children of a job member must be created INSIDE the job. This is the
    /// whole point of the tracker (seeing the real app behind launcher
    /// stubs), and it regressed silently once before: setting
    /// JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK on the job made every child
    /// break away, reducing EnumerateDescendantPids to just the root pid.
    /// </summary>
    [Fact]
    public async Task EnumerateDescendantPids_SeesChildrenSpawnedByTheAssignedRoot()
    {
        using var tracker = new ChildProcessTracker();
        tracker.IsActive.Should().BeTrue("job creation should succeed on any supported Windows");

        // cmd runs two pings sequentially; the first (~1s) guarantees the
        // second is spawned well after the job assignment below, so it can't
        // race past the assignment and escape the job that way.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"ping 127.0.0.1 -n 2 >nul & ping 127.0.0.1 -n 4 >nul\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var root = Process.Start(psi)!;
        try
        {
            tracker.TryAssign(root.Handle).Should().BeTrue();

            var descendants = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                descendants = tracker.EnumerateDescendantPids().Count;
                if (descendants >= 2)
                {
                    break;
                }
                await Task.Delay(100);
            }

            descendants.Should().BeGreaterThanOrEqualTo(2,
                "the ping child of the job-assigned cmd must appear in the job alongside cmd itself");
        }
        finally
        {
            try
            {
                root.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited — fine.
            }
        }
    }
}
