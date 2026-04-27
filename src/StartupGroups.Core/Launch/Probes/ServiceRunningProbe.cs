using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Launch.Probes;

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

    public async Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        var serviceName = context.App.Service!;
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = _services.QueryStatus(serviceName);
            if (state == ServiceState.Running)
            {
                context.Logger.LogDebug("ServiceRunningProbe fired: service={Service}", serviceName);
                return true;
            }
            if (state == ServiceState.NotFound)
            {
                context.Logger.LogDebug("ServiceRunningProbe aborted: service not found {Service}", serviceName);
                return false;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }
}
