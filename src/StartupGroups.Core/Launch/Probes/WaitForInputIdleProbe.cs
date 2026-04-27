using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Launch.Probes;

[SupportedOSPlatform("windows")]
public sealed class WaitForInputIdleProbe : IReadinessProbe
{
    private static readonly TimeSpan PollInterval = Timeouts.ProbePollDefault;
    private static readonly int PerAttemptTimeoutMs = (int)Timeouts.WaitForInputIdlePerAttempt.TotalMilliseconds;

    public ReadinessSignal Signal => ReadinessSignal.WaitForInputIdle;

    public bool AppliesTo(ProbeContext context) => context.App.Kind != AppKind.Service;

    public async Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var pid in context.Session.EnumerateDescendantPids())
            {
                if (cancellationToken.IsCancellationRequested) return false;
                if (TryWaitForInputIdle(pid, context.Logger))
                {
                    context.Session.TryMarkInputIdle(DateTimeOffset.UtcNow);
                    context.Logger.LogDebug("WaitForInputIdleProbe fired: pid={Pid}", pid);
                    return true;
                }
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

    private static bool TryWaitForInputIdle(int pid, ILogger logger)
    {
        Process? p = null;
        try
        {
            p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return p.WaitForInputIdle(PerAttemptTimeoutMs);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception ex)
        {
            logger.LogTrace(ex, "WaitForInputIdle Win32 failure on pid={Pid}", pid);
            return false;
        }
        finally
        {
            p?.Dispose();
        }
    }
}
