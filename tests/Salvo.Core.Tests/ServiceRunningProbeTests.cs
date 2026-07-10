using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Launch;
using Salvo.Core.Launch.Probes;
using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.Core.Tests;

/// <summary>
/// Pins the ServiceRunningProbe readiness contract: fires on Running, aborts on
/// NotFound, and polls through transient states until Running. It takes its only
/// dependency through IServiceController, so a stub covers every branch.
/// </summary>
public sealed class ServiceRunningProbeTests
{
    [Fact]
    public async Task RunAsync_Fires_WhenServiceRunning()
    {
        var probe = new ServiceRunningProbe(new StubController(ServiceState.Running));
        (await probe.RunAsync(MakeCtx(), CancellationToken.None)).Should().Be(ProbeOutcome.Fired);
    }

    [Fact]
    public async Task RunAsync_FailsDefinitively_WhenServiceNotFound()
    {
        // Failed (not GaveUp): a nonexistent service can never reach
        // Running, and the definitive outcome is what lets the detector
        // short-circuit instead of polling out the readiness timeout.
        var probe = new ServiceRunningProbe(new StubController(ServiceState.NotFound));
        (await probe.RunAsync(MakeCtx(), CancellationToken.None)).Should().Be(ProbeOutcome.Failed);
    }

    [Fact]
    public async Task RunAsync_Fires_AfterPendingThenRunning()
    {
        var controller = new StubController(ServiceState.Pending, ServiceState.Running);
        var probe = new ServiceRunningProbe(controller);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        (await probe.RunAsync(MakeCtx(), cts.Token)).Should().Be(ProbeOutcome.Fired);
        controller.CallCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public void AppliesTo_OnlyForServiceKindWithName()
    {
        var probe = new ServiceRunningProbe(new StubController(ServiceState.Running));

        probe.AppliesTo(MakeCtx()).Should().BeTrue();
        probe.AppliesTo(MakeCtx(new AppEntry { Name = "x", Kind = AppKind.Executable })).Should().BeFalse();
        probe.AppliesTo(MakeCtx(new AppEntry { Name = "x", Kind = AppKind.Service, Service = "" })).Should().BeFalse();
    }

    private static ProbeContext MakeCtx(AppEntry? app = null)
    {
        var session = LaunchSession.Begin();
        return new ProbeContext(
            session,
            app ?? new AppEntry { Name = "Svc", Kind = AppKind.Service, Service = "Svc" },
            null,
            NullLogger.Instance);
    }

    private sealed class StubController : IServiceController
    {
        private readonly Queue<ServiceState> _states;
        private readonly ServiceState _last;
        public int CallCount { get; private set; }

        public StubController(params ServiceState[] states)
        {
            _states = new Queue<ServiceState>(states);
            _last = states.Length > 0 ? states[^1] : ServiceState.NotFound;
        }

        public ServiceState QueryStatus(string serviceName)
        {
            CallCount++;
            return _states.Count > 0 ? _states.Dequeue() : _last;
        }

        public bool TryStart(string serviceName, TimeSpan timeout, out string message)
        {
            message = string.Empty;
            return true;
        }

        public bool TryStop(string serviceName, TimeSpan timeout, out string message)
        {
            message = string.Empty;
            return true;
        }
    }
}
