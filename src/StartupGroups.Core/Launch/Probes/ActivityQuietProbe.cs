using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Models;
using StartupGroups.Core.Native;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Launch.Probes;

[SupportedOSPlatform("windows")]
public sealed class ActivityQuietProbe : IReadinessProbe
{
    private const double CpuThresholdPercent = ReadinessThresholds.ActivityQuietCpuPercent;
    private const double IoThresholdBytesPerSec = ReadinessThresholds.ActivityQuietIoBytesPerSecond;
    private const double MinMaxCpuSeen = ReadinessThresholds.ActivityQuietMinMaxCpuSeen;
    private static readonly TimeSpan QuietWindow = Timeouts.ActivityQuietWindow;
    private static readonly TimeSpan PollInterval = Timeouts.ProbePollActivity;

    public ReadinessSignal Signal => ReadinessSignal.ActivityQuiet;

    public bool AppliesTo(ProbeContext context) => context.App.Kind != AppKind.Service;

    public async Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken)
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
                if (!TrySample(pid, now, out var sample)) continue;
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
                        if (now - quietSince.Value >= QuietWindow)
                        {
                            context.Session.TryMarkQuiet(now);
                            context.Logger.LogDebug("ActivityQuietProbe fired: maxCpu={MaxCpu:F1}%", maxCpuSeen);
                            return true;
                        }
                    }
                    else
                    {
                        quietSince = null;
                    }
                }
            }

            lastTickAt = now;

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

    private readonly record struct Sample(TimeSpan TotalCpu, ulong IoBytes, DateTimeOffset At);
}
