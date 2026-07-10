using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Launch;
using Salvo.Core.Models;

namespace Salvo.Core.Tests;

public sealed class ReadinessDetectorTests
{
    [Fact]
    public async Task DetectAsync_FastestProbeWins()
    {
        // Wide gap (10ms vs 2s = 200x) so heavily contended CI runners can't
        // schedule the slow probe's continuation faster than the fast probe's.
        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromMilliseconds(10), fires: true),
            new FakeProbe(ReadinessSignal.WaitForInputIdle, TimeSpan.FromSeconds(2), fires: true),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin();
        var ctx = MakeContext(session);
        var result = await detector.DetectAsync(ctx, TimeSpan.FromSeconds(5));

        result.Outcome.Should().Be(LaunchOutcome.Ready);
        result.Signal.Should().Be(ReadinessSignal.MainWindowVisible);
        session.SignalFired.Should().Be(ReadinessSignal.MainWindowVisible);
        session.ReadyAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DetectAsync_TimesOut_WhenNoProbeFires()
    {
        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromSeconds(10), fires: false),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin();
        var ctx = MakeContext(session);
        var result = await detector.DetectAsync(ctx, TimeSpan.FromMilliseconds(250));

        result.Outcome.Should().Be(LaunchOutcome.TimedOut);
        result.Signal.Should().Be(ReadinessSignal.Timeout);
        session.ReadyAt.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_WhenNoProbesApplicable()
    {
        // Nothing observed anything — a 0ms fake "timeout" would pollute
        // benchmark aggregates; Unknown is the honest outcome.
        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromMilliseconds(10), fires: true, applies: false),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin();
        var ctx = MakeContext(session);
        var result = await detector.DetectAsync(ctx, TimeSpan.FromMilliseconds(500));

        result.Outcome.Should().Be(LaunchOutcome.Unknown);
        result.Signal.Should().Be(ReadinessSignal.None);
    }

    [Fact]
    public async Task DetectAsync_ShortCircuitsToFailed_WhenAllProbesDefinitivelyFail()
    {
        // A known-dead target (e.g. a service that doesn't exist) must not
        // poll out the whole readiness timeout: once every probe reports a
        // definitive failure and there is no process tree left to watch,
        // the detector returns Failed immediately.
        var probes = new IReadinessProbe[]
        {
            new FailingProbe(ReadinessSignal.ServiceRunning, TimeSpan.FromMilliseconds(20)),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin(); // RootPid stays null
        var ctx = MakeContext(session);

        var sw = Stopwatch.StartNew();
        var result = await detector.DetectAsync(ctx, TimeSpan.FromSeconds(30));
        sw.Stop();

        result.Outcome.Should().Be(LaunchOutcome.Failed);
        result.Signal.Should().Be(ReadinessSignal.None);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the detector must not ride out the 30s timeout once the answer is definitive");
    }

    [Fact]
    public async Task DetectAsync_CancelsOtherProbes_WhenOneFires()
    {
        var slow = new FakeProbe(ReadinessSignal.ActivityQuiet, TimeSpan.FromSeconds(5), fires: true);
        var fast = new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromMilliseconds(20), fires: true);
        var detector = new ReadinessDetector(new IReadinessProbe[] { slow, fast });

        using var session = LaunchSession.Begin();
        var ctx = MakeContext(session);
        var result = await detector.DetectAsync(ctx, TimeSpan.FromSeconds(2));

        result.Signal.Should().Be(ReadinessSignal.MainWindowVisible);
        // Wait for the slow probe to observe its cancellation; on a slow runner the
        // probe's catch block can lag behind DetectAsync's return.
        await slow.Completed.WaitAsync(TimeSpan.FromSeconds(2));
        slow.WasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task DetectAsync_DoesNotReportExitedEarly_WhenRootPidNeverResolves()
    {
        // Regression: a shell launch resolves its PID asynchronously. Until it
        // lands, the process tree enumerates empty — which must NOT be read as
        // ExitedEarly at the 1s grace mark, or every slow-to-appear app is
        // falsely aborted. With no PID ever resolved and no probe firing, the
        // honest outcome is TimedOut, not ExitedEarly.
        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromSeconds(30), fires: false),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin(); // RootPid stays null
        var ctx = MakeContext(session);

        // Timeout comfortably past the 1s early-exit grace, so the buggy path
        // would already have returned ExitedEarly.
        var result = await detector.DetectAsync(ctx, TimeSpan.FromMilliseconds(1500));

        result.Outcome.Should().Be(LaunchOutcome.TimedOut);
    }

    [Fact]
    public async Task DetectAsync_ReportsExitedEarly_WhenResolvedPidTreeIsDead()
    {
        // Complement to the null-PID case: once a PID HAS resolved and its tree
        // is dead, ExitedEarly is still correct (an attached process that
        // crashed within the grace window). Guards the RootPid gate from
        // suppressing legitimate early-exit detection.
        using var dead = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        dead.WaitForExit();

        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromSeconds(30), fires: false),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin();
        session.RecordPidResolved(dead.Id);
        var ctx = MakeContext(session);

        var result = await detector.DetectAsync(ctx, TimeSpan.FromSeconds(3));

        result.Outcome.Should().Be(LaunchOutcome.ExitedEarly);
        result.Signal.Should().Be(ReadinessSignal.EarlyExit);
    }

    [Fact]
    public async Task DetectAsync_ProbeThrows_IsSwallowed_AndTimesOut()
    {
        // A throwing probe must not surface its exception; DetectAsync treats it
        // as Unknown. With a sub-grace timeout the early-exit watcher is cancelled
        // inside its 1s grace delay (so it can't win with ExitedEarly), leaving
        // TimedOut as the outcome.
        var probes = new IReadinessProbe[] { new ThrowingProbe(ReadinessSignal.MainWindowVisible) };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin();
        var ctx = MakeContext(session);

        var result = await detector.DetectAsync(ctx, TimeSpan.FromMilliseconds(250));

        result.Outcome.Should().Be(LaunchOutcome.TimedOut);
    }

    private static ProbeContext MakeContext(LaunchSession session) =>
        new(session, new AppEntry { Name = "Test", Path = @"C:\test.exe" }, @"C:\test.exe", NullLogger.Instance);

    private sealed class ThrowingProbe(ReadinessSignal signal) : IReadinessProbe
    {
        public ReadinessSignal Signal { get; } = signal;
        public bool AppliesTo(ProbeContext context) => true;

        public async Task<ProbeOutcome> RunAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("probe boom");
        }
    }

    private sealed class FailingProbe(ReadinessSignal signal, TimeSpan delay) : IReadinessProbe
    {
        public ReadinessSignal Signal { get; } = signal;
        public bool AppliesTo(ProbeContext context) => true;

        public async Task<ProbeOutcome> RunAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return ProbeOutcome.Failed;
        }
    }

    private sealed class FakeProbe : IReadinessProbe
    {
        private readonly TimeSpan _delay;
        private readonly bool _fires;
        private readonly bool _applies;
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProbe(ReadinessSignal signal, TimeSpan delay, bool fires, bool applies = true)
        {
            Signal = signal;
            _delay = delay;
            _fires = fires;
            _applies = applies;
        }

        public ReadinessSignal Signal { get; }
        public bool WasCancelled { get; private set; }
        public Task Completed => _completed.Task;

        public bool AppliesTo(ProbeContext context) => _applies;

        public async Task<ProbeOutcome> RunAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                return _fires ? ProbeOutcome.Fired : ProbeOutcome.GaveUp;
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                return ProbeOutcome.GaveUp;
            }
            finally
            {
                _completed.TrySetResult();
            }
        }
    }
}
