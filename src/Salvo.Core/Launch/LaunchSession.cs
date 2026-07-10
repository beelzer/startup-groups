using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Native;

namespace Salvo.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class LaunchSession : IDisposable
{
    private readonly object _lock = new();
    private readonly ChildProcessTracker _tracker;
    private readonly ILogger _logger;

    private DateTimeOffset? _processStartReturnedAt;
    private DateTimeOffset? _pidResolvedAt;
    private DateTimeOffset? _mainWindowAt;
    private DateTimeOffset? _inputIdleAt;
    private DateTimeOffset? _quietAt;
    private DateTimeOffset? _readyAt;
    private ReadinessSignal _signalFired;
    private int? _rootPid;
    private bool _jobAssigned;
    private bool _disposed;

    public Guid LaunchId { get; }
    public DateTimeOffset RequestedAt { get; }

    public int? RootPid { get { lock (_lock) return _rootPid; } }
    public DateTimeOffset? ProcessStartReturnedAt { get { lock (_lock) return _processStartReturnedAt; } }
    public DateTimeOffset? PidResolvedAt { get { lock (_lock) return _pidResolvedAt; } }
    public DateTimeOffset? MainWindowAt { get { lock (_lock) return _mainWindowAt; } }
    public DateTimeOffset? InputIdleAt { get { lock (_lock) return _inputIdleAt; } }
    public DateTimeOffset? QuietAt { get { lock (_lock) return _quietAt; } }
    public DateTimeOffset? ReadyAt { get { lock (_lock) return _readyAt; } }
    public ReadinessSignal SignalFired { get { lock (_lock) return _signalFired; } }

    private LaunchSession(Guid launchId, DateTimeOffset requestedAt, ILogger logger)
    {
        LaunchId = launchId;
        RequestedAt = requestedAt;
        _logger = logger;
        _tracker = new ChildProcessTracker(logger);
    }

    public static LaunchSession Begin(ILogger? logger = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, logger ?? NullLogger.Instance);

    public void AttachRootProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _processStartReturnedAt ??= DateTimeOffset.UtcNow;

            try
            {
                _rootPid = process.Id;
                _pidResolvedAt ??= DateTimeOffset.UtcNow;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                var handle = process.Handle;
                _jobAssigned = _tracker.TryAssign(handle);
            }
            catch (InvalidOperationException)
            {
                _jobAssigned = false;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogDebug(ex, "Cannot obtain process handle for job assignment");
                _jobAssigned = false;
            }
        }
    }

    public void RecordPidResolved(int pid)
    {
        lock (_lock)
        {
            if (_rootPid is null)
            {
                _rootPid = pid;
                _pidResolvedAt ??= DateTimeOffset.UtcNow;
            }
        }
    }

    // Every readiness probe re-enumerates the descendant PID set each poll, and
    // several probes poll near-simultaneously. A short TTL lets those overlapping
    // reads share one process-tree walk. The staleness (≤ TTL) is negligible for
    // liveness/quiet detection.
    private const long PidCacheTtlMs = 200;
    private readonly object _pidCacheLock = new();
    private IReadOnlyList<int>? _cachedPids;
    private long _cachedPidsAtMs = long.MinValue;

    public IReadOnlyList<int> EnumerateDescendantPids()
    {
        var now = Environment.TickCount64;
        lock (_pidCacheLock)
        {
            if (_cachedPids is not null && now - _cachedPidsAtMs < PidCacheTtlMs)
            {
                return _cachedPids;
            }
        }

        var fresh = EnumerateDescendantPidsUncached();

        lock (_pidCacheLock)
        {
            _cachedPids = fresh;
            _cachedPidsAtMs = now;
        }

        return fresh;
    }

    private IReadOnlyList<int> EnumerateDescendantPidsUncached()
    {
        int? root;
        bool jobAssigned;
        lock (_lock)
        {
            root = _rootPid;
            jobAssigned = _jobAssigned;
        }

        if (jobAssigned)
        {
            var fromJob = _tracker.EnumerateDescendantPids();
            if (fromJob.Count > 0)
            {
                return fromJob;
            }
        }

        if (root is int rootPid)
        {
            var fromSnapshot = ProcessTreeSnapshot.GetDescendantPids(rootPid);
            if (fromSnapshot.Count > 0)
            {
                var combined = new List<int>(fromSnapshot.Count + 1) { rootPid };
                combined.AddRange(fromSnapshot);
                return combined;
            }
            return new[] { rootPid };
        }

        return Array.Empty<int>();
    }

    public bool IsTreeAlive()
    {
        var pids = EnumerateDescendantPids();
        foreach (var pid in pids)
        {
            if (pid <= 0) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
        return false;
    }

    public bool TryMarkMainWindow(DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_mainWindowAt is not null) return false;
            _mainWindowAt = at;
            return true;
        }
    }

    public bool TryMarkInputIdle(DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_inputIdleAt is not null) return false;
            _inputIdleAt = at;
            return true;
        }
    }

    public bool TryMarkQuiet(DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_quietAt is not null) return false;
            _quietAt = at;
            return true;
        }
    }

    public bool TryMarkReady(DateTimeOffset at, ReadinessSignal signal)
    {
        lock (_lock)
        {
            if (_readyAt is not null) return false;
            _readyAt = at;
            _signalFired = signal;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _tracker.Dispose();
    }
}
