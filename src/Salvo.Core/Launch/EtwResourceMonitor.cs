using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Salvo.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class EtwResourceMonitor : IDisposable
{
    private const string SessionName = "Salvo.FileIO";
    private const int MaxEvents = 50_000;

    private readonly ILogger _logger;
    private readonly Queue<FileEvent> _events = new();
    private readonly object _lock = new();
    // Serializes session start against Dispose: TryStart runs on a
    // background task, and a Dispose racing past it would strand the
    // *named* kernel ETW session beyond process scope.
    private readonly object _lifecycleLock = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private bool _disposed;

    public bool IsActive => _session is not null;

    public EtwResourceMonitor(ILogger<EtwResourceMonitor>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        // Defer the actual ETW kernel-session setup off the calling thread.
        // TraceEventSession ctor + EnableKernelProvider does kernel calls
        // and is JIT-heavy on first hit (TraceEvent is a large library);
        // it adds ~1-3s on the cold path if run inline. The session is
        // background-only by design — no consumer needs IsActive to be
        // true synchronously after construction.
        Task.Run(TryStart);
    }

    public IReadOnlyList<string> QueryWindow(ISet<int> pids, DateTimeOffset from, DateTimeOffset to)
    {
        if (!IsActive || pids.Count == 0) return Array.Empty<string>();

        // Push events still sitting in kernel buffers through to the
        // dispatch callback before reading — without this, the tail of a
        // short launch window is systematically missing.
        try { _session?.Flush(); }
        catch (Exception ex) { _logger.LogDebug(ex, "ETW flush failed"); }

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var e in _events)
            {
                if (e.At < from) continue;
                // No early break: the queue merges per-CPU buffers, so
                // arrival order is not globally monotonic in event time.
                if (e.At > to) continue;
                if (pids.Contains(e.Pid) && !string.IsNullOrEmpty(e.Path))
                {
                    distinct.Add(e.Path);
                }
            }
        }
        return distinct.Count == 0 ? Array.Empty<string>() : distinct.ToArray();
    }

    private void TryStart()
    {
        if (!ElevationDetector.IsElevated)
        {
            _logger.LogInformation("ETW resource monitor disabled (process not elevated)");
            return;
        }

        lock (_lifecycleLock)
        {
            if (_disposed) return;

            try
            {
                _session = new TraceEventSession(SessionName)
                {
                    StopOnDispose = true,
                };
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit);
                _session.Source.Kernel.FileIOCreate += OnFileIOCreate;
                var session = _session;
                _processingTask = Task.Run(() =>
                {
                    try { session.Source.Process(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "ETW source processing stopped"); }
                });
                _logger.LogInformation("ETW resource monitor started");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start ETW resource monitor");
                _session?.Dispose();
                _session = null;
            }
        }
    }

    private void OnFileIOCreate(FileIOCreateTraceData data)
    {
        var path = data.FileName;
        if (string.IsNullOrEmpty(path)) return;
        var pid = data.ProcessID;
        if (pid <= 0) return;

        // Stamp with the event's own kernel timestamp, not dispatch-time
        // UtcNow: events sit in kernel buffers before delivery, and a
        // dispatch-time stamp pushes them past the query window's `to`.
        var at = new DateTimeOffset(data.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
        lock (_lock)
        {
            _events.Enqueue(new FileEvent(pid, path, at));
            while (_events.Count > MaxEvents) _events.Dequeue();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _session?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ETW session dispose threw"); }
            _session = null;
        }
    }

    private readonly record struct FileEvent(int Pid, string Path, DateTimeOffset At);
}
