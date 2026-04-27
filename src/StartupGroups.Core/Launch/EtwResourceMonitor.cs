using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class EtwResourceMonitor : IDisposable
{
    private const string SessionName = "StartupGroups.FileIO";
    private const int MaxEvents = 50_000;

    private readonly ILogger _logger;
    private readonly Queue<FileEvent> _events = new();
    private readonly object _lock = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private bool _disposed;

    public bool IsActive => _session is not null;

    public EtwResourceMonitor(ILogger<EtwResourceMonitor>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        TryStart();
    }

    public IReadOnlyList<string> QueryWindow(ISet<int> pids, DateTimeOffset from, DateTimeOffset to)
    {
        if (!IsActive || pids.Count == 0) return Array.Empty<string>();

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var e in _events)
            {
                if (e.At < from) continue;
                if (e.At > to) break;
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

        try
        {
            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
            };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit);
            _session.Source.Kernel.FileIOCreate += OnFileIOCreate;
            _processingTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
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

    private void OnFileIOCreate(FileIOCreateTraceData data)
    {
        var path = data.FileName;
        if (string.IsNullOrEmpty(path)) return;
        var pid = data.ProcessID;
        if (pid <= 0) return;

        lock (_lock)
        {
            _events.Enqueue(new FileEvent(pid, path, DateTimeOffset.UtcNow));
            while (_events.Count > MaxEvents) _events.Dequeue();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "ETW session dispose threw"); }
        _session = null;
    }

    private readonly record struct FileEvent(int Pid, string Path, DateTimeOffset At);
}
