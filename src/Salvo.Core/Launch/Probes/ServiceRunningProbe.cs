using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.Core.Launch.Probes;

[SupportedOSPlatform("windows")]
public sealed class ServiceRunningProbe : IReadinessProbe
{
    private static readonly TimeSpan PollInterval = Timeouts.ProbePollService;

    private readonly IServiceController _services;

    public ServiceRunningProbe(IServiceController services)
    {
        _services = services;
    }

    public ReadinessSignal Signal => ReadinessSignal.ServiceRunning;

    public bool AppliesTo(ProbeContext context) =>
        context.App.Kind == AppKind.Service && !string.IsNullOrWhiteSpace(context.App.Service);

    public async Task<ProbeOutcome> RunAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        var serviceName = context.App.Service!;
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = _services.QueryStatus(serviceName);
            if (state == ServiceState.Running)
            {
                context.Logger.LogDebug("ServiceRunningProbe fired: service={Service}", serviceName);
                return ProbeOutcome.Fired;
            }
            if (state == ServiceState.NotFound)
            {
                // Definitive: a service that doesn't exist can never reach
                // Running. Failed (not GaveUp) lets the detector stop
                // waiting instead of polling out the whole timeout.
                context.Logger.LogDebug("ServiceRunningProbe aborted: service not found {Service}", serviceName);
                return ProbeOutcome.Failed;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ProbeOutcome.GaveUp;
            }
        }
        return ProbeOutcome.GaveUp;
    }
}
