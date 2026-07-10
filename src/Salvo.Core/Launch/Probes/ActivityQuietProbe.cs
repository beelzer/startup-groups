using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Salvo.Core.Models;
using Salvo.Core.Native;
using Salvo.Core.Services;

namespace Salvo.Core.Launch.Probes;

[SupportedOSPlatform("windows")]
public sealed class ActivityQuietProbe : IReadinessProbe
{
    private const double CpuThresholdPercent = ReadinessThresholds.ActivityQuietCpuPercent;
    private const double IoThresholdBytesPerSec = ReadinessThresholds.ActivityQuietIoBytesPerSecond;
    private const double MinMaxCpuSeen = ReadinessThresholds.ActivityQuietMinMaxCpuSeen;

    internal delegate bool Sampler(int pid, DateTimeOffset now, out Sample sample);

    private readonly Sampler _sample;
    private readonly TimeSpan _quietWindow;
    private readonly TimeSpan _pollInterval;

    public ActivityQuietProbe()
        : this(TrySample, Timeouts.ActivityQuietWindow, Timeouts.ProbePollActivity)
    {
    }

    // Test seam: fake sampler + compressed timings, so the seeding and
    // quiet-window logic can be pinned without real processes.
    internal ActivityQuietProbe(Sampler sampler, TimeSpan quietWindow, TimeSpan pollInterval)
    {
        _sample = sampler;
        _quietWindow = quietWindow;
        _pollInterval = pollInterval;
    }

    public ReadinessSignal Signal => ReadinessSignal.ActivityQuiet;

    public bool AppliesTo(ProbeContext context) => context.App.Kind != AppKind.Service;

    public async Task<ProbeOutcome> RunAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        var prior = new Dictionary<int, Sample>();
        DateTimeOffset? lastTickAt = null;
        DateTimeOffset? quietSince = null;
        var maxCpuSeen = 0.0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var pids = context.Session.EnumerateDescendantPids();

            var aggregatedCpuMs = 0.0;
            var aggregatedIoBytes = 0UL;
            var observedAny = false;
            var current = new Dictionary<int, Sample>(pids.Count);

            foreach (var pid in pids)
            {
                if (!_sample(pid, now, out var sample)) continue;
                current[pid] = sample;

                if (prior.TryGetValue(pid, out var earlier))
                {
                    var cpuDeltaMs = (sample.TotalCpu - earlier.TotalCpu).TotalMilliseconds;
                    if (cpuDeltaMs < 0) cpuDeltaMs = 0;
                    aggregatedCpuMs += cpuDeltaMs;

                    if (sample.IoBytes >= earlier.IoBytes)
                    {
                        aggregatedIoBytes += sample.IoBytes - earlier.IoBytes;
                    }
                    observedAny = true;
                }
                else
                {
                    // First sample of this pid: the cumulative CPU it burned
                    // before our first tick is startup activity the delta
                    // path can never see. Feed it into the activity gate so
                    // a fast-initializing windowless app (all its CPU spent
                    // pre-first-tick) doesn't hold maxCpuSeen at ~0 and ride
                    // out the whole readiness timeout.
                    var sinceLaunchMs = (now - context.Session.RequestedAt).TotalMilliseconds;
                    if (sinceLaunchMs > 0)
                    {
                        var startupCpuPercent = sample.TotalCpu.TotalMilliseconds / sinceLaunchMs * 100.0;
                        if (startupCpuPercent > maxCpuSeen) maxCpuSeen = startupCpuPercent;
                    }
                }
            }

            prior = current;

            if (observedAny && lastTickAt is DateTimeOffset last)
            {
                var wallDeltaMs = (now - last).TotalMilliseconds;
                if (wallDeltaMs > 0)
                {
                    var cpuPercent = aggregatedCpuMs / wallDeltaMs * 100.0;
                    var ioPerSec = aggregatedIoBytes / (wallDeltaMs / 1000.0);
                    if (cpuPercent > maxCpuSeen) maxCpuSeen = cpuPercent;

                    var isQuietNow = cpuPercent < CpuThresholdPercent && ioPerSec < IoThresholdBytesPerSec;
                    if (isQuietNow && maxCpuSeen >= MinMaxCpuSeen)
                    {
                        quietSince ??= now;
                        if (now - quietSince.Value >= _quietWindow)
                        {
                            context.Session.TryMarkQuiet(now);
                            context.Logger.LogDebug("ActivityQuietProbe fired: maxCpu={MaxCpu:F1}%", maxCpuSeen);
                            return ProbeOutcome.Fired;
                        }
                    }
                    else
                    {
                        quietSince = null;
                    }
                }
            }
            else if (!observedAny)
            {
                // A tick where no PID could be sampled (transient churn, all
                // descendants briefly unsampleable) breaks quiet continuity:
                // the window must only accrue across consecutive confirmed-quiet
                // ticks, otherwise a real activity burst landing on an unsampled
                // tick could be skipped and the probe fire 'quiet' prematurely.
                quietSince = null;
            }

            lastTickAt = now;

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ProbeOutcome.GaveUp;
            }
        }
        return ProbeOutcome.GaveUp;
    }

    private static bool TrySample(int pid, DateTimeOffset now, out Sample sample)
    {
        sample = default;
        Process? p = null;
        try
        {
            p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var cpu = p.TotalProcessorTime;
            var io = 0UL;
            if (ProcessIoInterop.TryReadIoCounters(pid, out var counters))
            {
                io = counters.ReadTransferCount + counters.WriteTransferCount;
            }
            sample = new Sample(cpu, io, now);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            p?.Dispose();
        }
    }

    internal readonly record struct Sample(TimeSpan TotalCpu, ulong IoBytes, DateTimeOffset At);
}
