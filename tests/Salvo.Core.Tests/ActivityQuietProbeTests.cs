using Salvo.Core.Launch;
using Salvo.Core.Launch.Probes;
using Salvo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Salvo.Core.Tests;

public sealed class ActivityQuietProbeTests
{
    /// <summary>
    /// A fast-initializing windowless app burns all its startup CPU before
    /// the probe's first ~500ms tick; measured purely as inter-sample
    /// deltas that CPU is invisible, maxCpuSeen stays ~0, and the
    /// MinMaxCpuSeen activity gate blocked firing forever — every such app
    /// rode out the full readiness timeout. The first sample's cumulative
    /// TotalProcessorTime must seed the gate instead.
    /// </summary>
    [Fact]
    public async Task RunAsync_SeedsActivityGate_FromFirstSampleCumulativeCpu()
    {
        // Constant cumulative CPU: all work happened before the first
        // sample; every delta after it is zero (perfectly quiet).
        static bool Sampler(int pid, DateTimeOffset now, out ActivityQuietProbe.Sample sample)
        {
            sample = new ActivityQuietProbe.Sample(TimeSpan.FromMilliseconds(300), 0UL, now);
            return true;
        }

        var probe = new ActivityQuietProbe(
            Sampler,
            quietWindow: TimeSpan.FromMilliseconds(80),
            pollInterval: TimeSpan.FromMilliseconds(10));

        using var session = LaunchSession.Begin();
        session.RecordPidResolved(999_999); // fake pid; the sampler never touches it
        var ctx = new ProbeContext(session, new AppEntry { Name = "fast-init" }, null, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outcome = await probe.RunAsync(ctx, cts.Token);

        outcome.Should().Be(ProbeOutcome.Fired,
            "startup CPU spent before the first tick must open the activity gate");
        session.QuietAt.Should().NotBeNull();
    }
}
