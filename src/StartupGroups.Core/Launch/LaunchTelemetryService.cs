using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class LaunchTelemetryService : ILaunchTelemetryService
{
    public static readonly TimeSpan DefaultReadinessTimeout = Timeouts.ReadinessDefault;
    private static readonly TimeSpan PidResolveTimeout = Timeouts.PidResolveDeadline;
    private static readonly TimeSpan PidResolvePollInterval = Timeouts.PidResolvePoll;

    private readonly ReadinessDetector _detector;
    private readonly ILaunchBenchmarkStore _store;
    private readonly IProcessInspector? _inspector;
    private readonly IProcessMatcherResolver? _matchers;
    private readonly EtwResourceMonitor? _etw;
    private readonly ILogger<LaunchTelemetryService> _logger;
    private readonly TimeSpan _timeout;

    public event EventHandler<LaunchMetrics>? MetricsSaved;

    public LaunchTelemetryService(
        ReadinessDetector detector,
        ILaunchBenchmarkStore store,
        IProcessInspector? inspector = null,
        IProcessMatcherResolver? matchers = null,
        EtwResourceMonitor? etw = null,
        ILogger<LaunchTelemetryService>? logger = null,
        TimeSpan? timeout = null)
    {
        _detector = detector;
        _store = store;
        _inspector = inspector;
        _matchers = matchers;
        _etw = etw;
        _logger = logger ?? NullLogger<LaunchTelemetryService>.Instance;
        _timeout = timeout ?? DefaultReadinessTimeout;
    }

    public Task<LaunchMetrics> BeginObservation(AppEntry app, string? resolvedPath, string? groupId, Process? process)
    {
        ArgumentNullException.ThrowIfNull(app);

        var session = LaunchSession.Begin(_logger);
        if (process is not null)
        {
            try
            {
                session.AttachRootProcess(process);
            }
            finally
            {
                process.Dispose();
            }
        }
        else if (_inspector is not null && _matchers is not null)
        {
            _ = Task.Run(() => ResolvePidAsync(app, session));
        }

        return Task.Run(() => ObserveAsync(app, resolvedPath, groupId, session));
    }

    private async Task ResolvePidAsync(AppEntry app, LaunchSession session)
    {
        if (_inspector is null || _matchers is null) return;

        var matchers = _matchers.GetMatchers(app);
        if (matchers.Count == 0) return;

        var deadline = DateTimeOffset.UtcNow + PidResolveTimeout;
        while (DateTimeOffset.UtcNow < deadline && session.RootPid is null)
        {
            try
            {
                var pids = _inspector.FindMatchingPids(matchers);
                if (pids.Count > 0)
                {
                    session.RecordPidResolved(pids[0]);
                    _logger.LogDebug("Resolved PID {Pid} for shell-launched {App}", pids[0], app.Name);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PID resolver failed for {App}", app.Name);
            }

            try { await Task.Delay(PidResolvePollInterval).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private async Task<LaunchMetrics> ObserveAsync(AppEntry app, string? resolvedPath, string? groupId, LaunchSession session)
    {
        var appId = AppIdentity.ComputeAppId(resolvedPath, app.Name);
        try
        {
            var hadPriorReady = await _store.HasReadyLaunchSinceBootAsync(appId, BootSession.BootEpochUtc).ConfigureAwait(false);
            var isCold = !hadPriorReady;

            var context = new ProbeContext(session, app, resolvedPath, _logger);
            var result = await _detector.DetectAsync(context, _timeout).ConfigureAwait(false);

            var metrics = BuildMetrics(app, resolvedPath, groupId, appId, isCold, session, result);
            await _store.SaveAsync(metrics).ConfigureAwait(false);

            await TryCaptureResourcesAsync(session, metrics).ConfigureAwait(false);

            _logger.LogInformation(
                "Launch observed: {App} outcome={Outcome} signal={Signal} duration={DurationMs}ms cold={IsCold}",
                app.Name, metrics.Outcome, metrics.SignalFired,
                metrics.TotalDuration?.TotalMilliseconds ?? -1, metrics.IsCold);

            try { MetricsSaved?.Invoke(this, metrics); }
            catch (Exception ex) { _logger.LogWarning(ex, "MetricsSaved handler threw"); }

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Launch observation failed for {App}", app.Name);
            return new LaunchMetrics
            {
                LaunchId = session.LaunchId,
                AppId = appId,
                AppName = app.Name,
                GroupId = groupId,
                ResolvedPath = resolvedPath,
                RequestedAt = session.RequestedAt,
                Outcome = LaunchOutcome.Unknown,
                SignalFired = ReadinessSignal.None,
                IsCold = false,
                BootEpochUtc = BootSession.BootEpochUtc,
            };
        }
        finally
        {
            session.Dispose();
        }
    }

    private async Task TryCaptureResourcesAsync(LaunchSession session, LaunchMetrics metrics)
    {
        if (_etw is null || !_etw.IsActive) return;

        var endAt = metrics.ReadyAt ?? DateTimeOffset.UtcNow;
        var pids = new HashSet<int>(session.EnumerateDescendantPids());
        if (session.RootPid is int root) pids.Add(root);
        if (pids.Count == 0) return;

        var paths = _etw.QueryWindow(pids, metrics.RequestedAt, endAt);
        if (paths.Count == 0) return;

        try
        {
            await _store.SaveResourcesAsync(metrics.LaunchId, paths).ConfigureAwait(false);
            _logger.LogDebug("Captured {Count} file opens for {App}", paths.Count, metrics.AppName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SaveResourcesAsync failed");
        }
    }

    private static LaunchMetrics BuildMetrics(
        AppEntry app,
        string? resolvedPath,
        string? groupId,
        string appId,
        bool isCold,
        LaunchSession session,
        ReadinessResult result)
    {
        return new LaunchMetrics
        {
            LaunchId = session.LaunchId,
            AppId = appId,
            AppName = app.Name,
            GroupId = groupId,
            ResolvedPath = resolvedPath,
            ResolvedPathHash = string.IsNullOrEmpty(resolvedPath) ? null : AppIdentity.ComputePathHash(resolvedPath),
            RootPid = session.RootPid,
            RequestedAt = session.RequestedAt,
            ProcessStartReturnedAt = session.ProcessStartReturnedAt,
            PidResolvedAt = session.PidResolvedAt,
            MainWindowAt = session.MainWindowAt,
            InputIdleAt = session.InputIdleAt,
            QuietAt = session.QuietAt,
            ReadyAt = session.ReadyAt,
            Outcome = result.Outcome,
            SignalFired = result.Signal,
            IsCold = isCold,
            BootEpochUtc = BootSession.BootEpochUtc,
        };
    }
}
