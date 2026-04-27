using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Launch;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class AppOrchestrator : IAppOrchestrator
{
    private static readonly TimeSpan ServiceOperationTimeout = Timeouts.OrchestratorServiceOperation;

    private readonly IPathResolver _pathResolver;
    private readonly IProcessLauncher _launcher;
    private readonly IProcessInspector _inspector;
    private readonly IProcessMatcherResolver _matchers;
    private readonly IServiceController _services;
    private readonly ILaunchTelemetryService? _telemetry;
    private readonly ILogger<AppOrchestrator> _logger;

    public AppOrchestrator(
        IPathResolver pathResolver,
        IProcessLauncher launcher,
        IProcessInspector inspector,
        IProcessMatcherResolver matchers,
        IServiceController services,
        ILaunchTelemetryService? telemetry = null,
        ILogger<AppOrchestrator>? logger = null)
    {
        _pathResolver = pathResolver;
        _launcher = launcher;
        _inspector = inspector;
        _matchers = matchers;
        _services = services;
        _telemetry = telemetry;
        _logger = logger ?? NullLogger<AppOrchestrator>.Instance;
    }

    public bool IsRunning(AppEntry app)
    {
        if (app.Kind == AppKind.Service)
        {
            return !string.IsNullOrEmpty(app.Service)
                && _services.QueryStatus(app.Service) == ServiceState.Running;
        }

        var matchers = _matchers.GetMatchers(app);
        return matchers.Count > 0 && _inspector.IsRunning(matchers);
    }

    public OperationResult LaunchApp(AppEntry app)
    {
        var (result, _) = LaunchAppCore(app, groupId: null);
        return result;
    }

    public OperationResult LaunchApp(AppEntry app, string? groupId)
    {
        var (result, _) = LaunchAppCore(app, groupId);
        return result;
    }

    private (OperationResult Result, Task<LaunchMetrics>? Observation) LaunchAppCore(AppEntry app, string? groupId)
    {
        if (!app.Enabled)
        {
            return (OperationResult.AlreadyInState("Disabled", app), null);
        }

        if (app.Kind == AppKind.Service)
        {
            if (string.IsNullOrWhiteSpace(app.Service))
            {
                return (OperationResult.Failed("No service name", app), null);
            }

            var state = _services.QueryStatus(app.Service);
            if (state == ServiceState.NotFound)
            {
                return (OperationResult.NotFound("Service not found", app), null);
            }

            if (state == ServiceState.Running)
            {
                return (OperationResult.AlreadyInState("Already running", app), null);
            }

            if (_services.TryStart(app.Service, ServiceOperationTimeout, out var message))
            {
                var obs = _telemetry?.BeginObservation(app, resolvedPath: null, groupId, process: null);
                return (OperationResult.Success(message, app), obs);
            }

            return (string.Equals(message, "Needs admin", StringComparison.OrdinalIgnoreCase)
                ? OperationResult.NeedsElevation(app)
                : OperationResult.Failed(message, app), null);
        }

        var resolved = _pathResolver.Resolve(app.Path);
        if (resolved is null)
        {
            return (OperationResult.NotFound("File not found", app), null);
        }

        var matchers = _matchers.GetMatchers(app);
        if (matchers.Count > 0 && _inspector.IsRunning(matchers))
        {
            return (OperationResult.AlreadyInState("Already running", app), null);
        }

        if (_launcher.TryStartAndCapture(app, resolved, out var process, out var launchMessage))
        {
            var obs = _telemetry?.BeginObservation(app, resolved, groupId, process);
            return (OperationResult.Success(launchMessage, app), obs);
        }

        process?.Dispose();
        return (OperationResult.Failed(launchMessage, app), null);
    }

    public OperationResult StopApp(AppEntry app)
    {
        if (app.Kind == AppKind.Service)
        {
            if (string.IsNullOrWhiteSpace(app.Service))
            {
                return OperationResult.Failed("No service name", app);
            }

            var state = _services.QueryStatus(app.Service);
            if (state == ServiceState.NotFound)
            {
                return OperationResult.NotFound("Service not found", app);
            }

            if (state == ServiceState.Stopped)
            {
                return OperationResult.AlreadyInState("Already stopped", app);
            }

            if (_services.TryStop(app.Service, ServiceOperationTimeout, out var message))
            {
                return OperationResult.Success(message, app);
            }

            return string.Equals(message, "Needs admin", StringComparison.OrdinalIgnoreCase)
                ? OperationResult.NeedsElevation(app)
                : OperationResult.Failed(message, app);
        }

        var matchers = _matchers.GetMatchers(app);
        if (matchers.Count == 0)
        {
            return OperationResult.Failed("No path", app);
        }

        if (!_inspector.IsRunning(matchers))
        {
            return OperationResult.AlreadyInState("Not running", app);
        }

        if (_inspector.TryKill(matchers, out var killMessage))
        {
            return OperationResult.Success(killMessage, app);
        }

        return OperationResult.Failed(killMessage, app);
    }

    public async Task<IReadOnlyList<OperationResult>> LaunchGroupAsync(Group group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var results = new List<OperationResult>(group.Apps.Count);
        var waveObservations = new List<Task<LaunchMetrics>>();
        var waveStartedAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < group.Apps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var app = group.Apps[i];
            var (result, obs) = LaunchAppCore(app, group.Id);
            results.Add(result);
            _logger.LogInformation("Launch {AppName}: {Status} - {Message}", app.Name, result.Status, result.Message);
            if (obs is not null)
            {
                waveObservations.Add(obs);
            }

            var isLast = i == group.Apps.Count - 1;
            var closesWave = app.DelayAfterSeconds > 0 || isLast;

            if (closesWave)
            {
                if (waveObservations.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(waveObservations).WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "One or more wave observations faulted");
                    }
                }

                if (!isLast && app.DelayAfterSeconds > 0)
                {
                    var elapsed = DateTimeOffset.UtcNow - waveStartedAt;
                    var floor = TimeSpan.FromSeconds(app.DelayAfterSeconds);
                    var remaining = floor - elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                    }
                }

                waveObservations.Clear();
                waveStartedAt = DateTimeOffset.UtcNow;
            }
        }

        return results;
    }

    public Task<IReadOnlyList<OperationResult>> StopGroupAsync(Group group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var results = new List<OperationResult>(group.Apps.Count);
        foreach (var app in group.Apps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = StopApp(app);
            results.Add(result);
            _logger.LogInformation("Stop {AppName}: {Status} - {Message}", app.Name, result.Status, result.Message);
        }

        return Task.FromResult<IReadOnlyList<OperationResult>>(results);
    }
}
