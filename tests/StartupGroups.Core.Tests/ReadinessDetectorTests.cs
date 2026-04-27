using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Tests;

public sealed class ReadinessDetectorTests
{
    [Fact]
    public async Task DetectAsync_FastestProbeWins()
    {
        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromMilliseconds(50), fires: true),
            new FakeProbe(ReadinessSignal.WaitForInputIdle, TimeSpan.FromMilliseconds(500), fires: true),
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
    public async Task DetectAsync_ReturnsTimedOut_WhenNoProbesApplicable()
    {
        var probes = new IReadinessProbe[]
        {
            new FakeProbe(ReadinessSignal.MainWindowVisible, TimeSpan.FromMilliseconds(10), fires: true, applies: false),
        };
        var detector = new ReadinessDetector(probes);

        using var session = LaunchSession.Begin();
        var ctx = MakeContext(session);
        var result = await detector.DetectAsync(ctx, TimeSpan.FromMilliseconds(500));

        result.Outcome.Should().Be(LaunchOutcome.TimedOut);
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
        slow.WasCancelled.Should().BeTrue();
    }

    private static ProbeContext MakeContext(LaunchSession session) =>
        new(session, new AppEntry { Name = "Test", Path = @"C:\test.exe" }, @"C:\test.exe", NullLogger.Instance);

    private sealed class FakeProbe : IReadinessProbe
    {
        private readonly TimeSpan _delay;
        private readonly bool _fires;
        private readonly bool _applies;

        public FakeProbe(ReadinessSignal signal, TimeSpan delay, bool fires, bool applies = true)
        {
            Signal = signal;
            _delay = delay;
            _fires = fires;
            _applies = applies;
        }

        public ReadinessSignal Signal { get; }
        public bool WasCancelled { get; private set; }

        public bool AppliesTo(ProbeContext context) => _applies;

        public async Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                return _fires;
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                return false;
            }
        }
    }
}
