using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class ReadinessDetector
{
    private static readonly TimeSpan EarlyExitGrace = Timeouts.ReadinessEarlyExitGrace;
    private static readonly TimeSpan EarlyExitPollInterval = Timeouts.ReadinessEarlyExitPoll;

    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly ILogger<ReadinessDetector> _logger;

    public ReadinessDetector(IEnumerable<IReadinessProbe> probes, ILogger<ReadinessDetector>? logger = null)
    {
        _probes = probes.ToArray();
        _logger = logger ?? NullLogger<ReadinessDetector>.Instance;
    }

    public async Task<ReadinessResult> DetectAsync(
        ProbeContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        var applicable = _probes.Where(p => p.AppliesTo(context)).ToArray();
        if (applicable.Length == 0)
        {
            _logger.LogDebug("No applicable probes for {App}; marking timeout", context.App.Name);
            return new ReadinessResult(LaunchOutcome.TimedOut, ReadinessSignal.Timeout, DateTimeOffset.UtcNow);
        }

        var probeTasks = applicable
            .Select(p => WrapProbeAsync(p, context, linkedCts.Token))
            .ToList();

        var earlyExitTask = WatchEarlyExitAsync(context, linkedCts.Token);
        var allWatched = probeTasks.Concat(new[] { earlyExitTask }).ToList();

        while (allWatched.Count > 0)
        {
            var completed = await Task.WhenAny(allWatched).ConfigureAwait(false);
            allWatched.Remove(completed);

            var winner = await completed.ConfigureAwait(false);
            if (winner.Outcome == LaunchOutcome.Ready)
            {
                linkedCts.Cancel();
                context.Session.TryMarkReady(winner.ResolvedAt, winner.Signal);
                _logger.LogDebug("Readiness {Signal} won for {App}", winner.Signal, context.App.Name);
                return winner;
            }
            if (winner.Outcome == LaunchOutcome.ExitedEarly)
            {
                linkedCts.Cancel();
                _logger.LogDebug("Early exit detected for {App}", context.App.Name);
                return winner;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        _logger.LogDebug("Readiness timeout for {App}", context.App.Name);
        return new ReadinessResult(LaunchOutcome.TimedOut, ReadinessSignal.Timeout, DateTimeOffset.UtcNow);
    }

    private static async Task<ReadinessResult> WrapProbeAsync(
        IReadinessProbe probe,
        ProbeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var fired = await probe.RunAsync(context, cancellationToken).ConfigureAwait(false);
            if (fired)
            {
                return new ReadinessResult(LaunchOutcome.Ready, probe.Signal, DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "Probe {Signal} threw", probe.Signal);
        }
        return new ReadinessResult(LaunchOutcome.Unknown, ReadinessSignal.None, DateTimeOffset.UtcNow);
    }

    private static async Task<ReadinessResult> WatchEarlyExitAsync(
        ProbeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(EarlyExitGrace, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!context.Session.IsTreeAlive())
                {
                    return new ReadinessResult(LaunchOutcome.ExitedEarly, ReadinessSignal.EarlyExit, DateTimeOffset.UtcNow);
                }
                await Task.Delay(EarlyExitPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return new ReadinessResult(LaunchOutcome.Unknown, ReadinessSignal.None, DateTimeOffset.UtcNow);
    }
}
