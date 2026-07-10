using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Services;

namespace Salvo.Core.Launch;

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
            // Nothing observed anything — recording a 0ms "timeout" would
            // pollute benchmarks with fake timeouts; Unknown is honest.
            _logger.LogDebug("No applicable probes for {App}; readiness unknown", context.App.Name);
            return new ReadinessResult(LaunchOutcome.Unknown, ReadinessSignal.None, DateTimeOffset.UtcNow);
        }

        var probeTasks = applicable
            .Select(p => WrapProbeAsync(p, context, linkedCts.Token))
            .ToList();

        var earlyExitTask = WatchEarlyExitAsync(context, linkedCts.Token);
        var allWatched = probeTasks.Concat(new[] { earlyExitTask }).ToList();

        var probeSet = new HashSet<Task<ReadinessResult>>(probeTasks);
        var probesRemaining = probeTasks.Count;
        var probesFailed = 0;

        while (allWatched.Count > 0)
        {
            var completed = await Task.WhenAny(allWatched).ConfigureAwait(false);
            allWatched.Remove(completed);

            var winner = await completed.ConfigureAwait(false);
            if (winner.Outcome == LaunchOutcome.Ready)
            {
                context.Session.TryMarkReady(winner.ResolvedAt, winner.Signal);
                _logger.LogDebug("Readiness {Signal} won for {App}", winner.Signal, context.App.Name);
                return await CancelAndDrainAsync(linkedCts, allWatched, winner).ConfigureAwait(false);
            }
            if (winner.Outcome == LaunchOutcome.ExitedEarly)
            {
                _logger.LogDebug("Early exit detected for {App}", context.App.Name);
                return await CancelAndDrainAsync(linkedCts, allWatched, winner).ConfigureAwait(false);
            }

            if (probeSet.Contains(completed))
            {
                probesRemaining--;
                if (winner.Outcome == LaunchOutcome.Failed) probesFailed++;

                // Every probe has definitively failed (e.g. a service that
                // doesn't exist) and there is no process tree the early-exit
                // watcher could still conclude anything from — waiting out
                // the rest of the timeout cannot change the answer.
                if (probesRemaining == 0
                    && probesFailed == probeTasks.Count
                    && context.Session.RootPid is null)
                {
                    _logger.LogDebug("All probes definitively failed for {App}; short-circuiting", context.App.Name);
                    var failed = new ReadinessResult(LaunchOutcome.Failed, ReadinessSignal.None, DateTimeOffset.UtcNow);
                    return await CancelAndDrainAsync(linkedCts, allWatched, failed).ConfigureAwait(false);
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        _logger.LogDebug("Readiness timeout for {App}", context.App.Name);
        return new ReadinessResult(LaunchOutcome.TimedOut, ReadinessSignal.Timeout, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Cancel the losers and wait for them to actually finish before
    /// returning: the caller disposes the session (and with it the job
    /// handle) right after, and a still-running probe touching
    /// <c>EnumerateDescendantPids</c> past that point is a check-then-use
    /// on a closed, OS-recyclable Win32 handle. The wrapped tasks never
    /// throw and exit promptly on cancellation.
    /// </summary>
    private static async Task<ReadinessResult> CancelAndDrainAsync(
        CancellationTokenSource cts,
        List<Task<ReadinessResult>> pending,
        ReadinessResult result)
    {
        cts.Cancel();
        if (pending.Count > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<ReadinessResult> WrapProbeAsync(
        IReadinessProbe probe,
        ProbeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await probe.RunAsync(context, cancellationToken).ConfigureAwait(false);
            return outcome switch
            {
                ProbeOutcome.Fired => new ReadinessResult(LaunchOutcome.Ready, probe.Signal, DateTimeOffset.UtcNow),
                ProbeOutcome.Failed => new ReadinessResult(LaunchOutcome.Failed, ReadinessSignal.None, DateTimeOffset.UtcNow),
                _ => new ReadinessResult(LaunchOutcome.Unknown, ReadinessSignal.None, DateTimeOffset.UtcNow),
            };
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
                // Only conclude ExitedEarly once a PID has actually resolved and
                // the tree is then dead. Shell launches resolve their PID
                // asynchronously (up to PidResolveDeadline); until then the tree
                // enumerates empty, and reading that as "exited" falsely aborts
                // every slow-to-appear app at the 1s grace mark. Attached-process
                // launches set RootPid synchronously, so a genuine crash within
                // the grace window is still caught here. A shell launch whose
                // process never resolves now falls through to TimedOut, which is
                // honest — the two are indistinguishable without a PID.
                if (context.Session.RootPid is not null && !context.Session.IsTreeAlive())
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
