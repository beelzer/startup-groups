using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Launch;
using Salvo.Core.Models;
using Salvo.Core.Models.Flow;

namespace Salvo.Core.Services;

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

        // Defensive: if a caller hands us a group that only has the
        // legacy Apps list populated (e.g. tests that build a Group
        // directly without going through JsonConfigStore), build the
        // graph on the fly so we still execute the apps.
        if (group.Nodes.Count == 0 && group.Apps.Count > 0)
        {
            FlowMigration.Migrate(group);
        }

        return await ExecuteGraphAsync(group, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OperationResult>> StopGroupAsync(Group group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Stop only touches AppNode entries; control-flow nodes (Wait,
        // If/Else, Start) have nothing to stop. Order doesn't matter
        // here — just stop everything once.
        var apps = group.Nodes.OfType<AppNode>().Select(n => n.App).ToList();
        var results = new List<OperationResult>(apps.Count);
        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = StopApp(app);
            results.Add(result);
            _logger.LogInformation("Stop {AppName}: {Status} - {Message}", app.Name, result.Status, result.Message);
        }

        return Task.FromResult<IReadOnlyList<OperationResult>>(results);
    }

    /// <summary>
    /// Walk the group's flow graph, executing each node when all its
    /// incoming edges have completed.
    ///
    /// Semantics (matches the docstrings on <see cref="Edge"/> and the
    /// node types):
    /// - A node runs when all of its incoming edges are non-pending and
    ///   at least one is "fired" (i.e. its upstream node ran). If every
    ///   incoming was "skipped" (the IfElse branch wasn't taken), the
    ///   node itself is skipped — all its outgoing edges propagate skip.
    /// - Outgoing edges of a normal node all fire in parallel after the
    ///   node completes (which for an AppNode means *both* the launch
    ///   call returned *and* the readiness observation settled).
    /// - <see cref="IfElseNode"/> evaluates its condition and fires the
    ///   matching label ("then"/"else"); the unmatched label is skipped.
    /// </summary>
    private async Task<IReadOnlyList<OperationResult>> ExecuteGraphAsync(Group group, CancellationToken cancellationToken)
    {
        if (group.Nodes.Count == 0)
        {
            return [];
        }

        var nodeById = group.Nodes.ToDictionary(n => n.Id);
        var outgoing = group.Nodes.ToDictionary(n => n.Id, _ => new List<Edge>());
        var pendingIn = group.Nodes.ToDictionary(n => n.Id, _ => 0);
        var firedIn = group.Nodes.ToDictionary(n => n.Id, _ => 0);

        foreach (var edge in group.Edges)
        {
            if (!nodeById.ContainsKey(edge.From) || !nodeById.ContainsKey(edge.To))
            {
                _logger.LogWarning("Edge {EdgeId} references unknown node ({From} → {To}); skipping", edge.Id, edge.From, edge.To);
                continue;
            }
            outgoing[edge.From].Add(edge);
            pendingIn[edge.To]++;
        }

        var results = new List<OperationResult>();
        var resultsLock = new Lock();
        var running = new List<Task>();
        var readyQueue = new Queue<Node>();
        var executed = new HashSet<string>();
        var skipped = new HashSet<string>();

        // Nodes with no incoming edges are immediately ready. Typically
        // this is just the Start node, but we tolerate any source node.
        foreach (var node in group.Nodes)
        {
            if (pendingIn[node.Id] == 0)
            {
                readyQueue.Enqueue(node);
            }
        }

        while (readyQueue.Count > 0 || running.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (readyQueue.TryDequeue(out var node))
            {
                if (executed.Contains(node.Id) || skipped.Contains(node.Id))
                {
                    continue;
                }

                if (firedIn[node.Id] == 0 && node is not StartNode)
                {
                    // All incoming edges were skipped (or there are none
                    // and this isn't the Start node — orphan). Skip the
                    // node itself and propagate.
                    skipped.Add(node.Id);
                    PropagateSkip(node);
                    continue;
                }

                executed.Add(node.Id);
                running.Add(RunNodeAsync(node));
            }

            if (running.Count == 0)
            {
                break;
            }

            var done = await Task.WhenAny(running).ConfigureAwait(false);
            running.Remove(done);
            await done.ConfigureAwait(false); // re-throw any cancellation
        }

        return results;

        // -- locals --

        async Task RunNodeAsync(Node node)
        {
            try
            {
                switch (node)
                {
                    case StartNode:
                        // Pure marker — nothing to do, just fan out.
                        break;

                    case AppNode appNode:
                    {
                        var (result, obs) = LaunchAppCore(appNode.App, group.Id);
                        lock (resultsLock) { results.Add(result); }
                        _logger.LogInformation("Launch {AppName}: {Status} - {Message}", appNode.App.Name, result.Status, result.Message);
                        if (obs is not null)
                        {
                            try
                            {
                                await obs.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Readiness observation for {AppName} faulted", appNode.App.Name);
                            }
                        }
                        break;
                    }

                    case WaitNode waitNode:
                    {
                        if (waitNode.DurationSeconds > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(waitNode.DurationSeconds), cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }

                    case IfElseNode ifElse:
                    {
                        var taken = EvaluateCondition(ifElse.Condition) ? "then" : "else";
                        FireOutgoing(ifElse, edge => edge.Label == taken);
                        return; // skip the default fan-out below
                    }
                }

                FireOutgoing(node, _ => true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        void FireOutgoing(Node from, Predicate<Edge> fire)
        {
            foreach (var edge in outgoing[from.Id])
            {
                if (fire(edge))
                {
                    pendingIn[edge.To]--;
                    firedIn[edge.To]++;
                }
                else
                {
                    pendingIn[edge.To]--;
                    // firedIn not incremented — counts as skip from this edge
                }

                if (pendingIn[edge.To] == 0 && !executed.Contains(edge.To) && !skipped.Contains(edge.To))
                {
                    readyQueue.Enqueue(nodeById[edge.To]);
                }
            }
        }

        void PropagateSkip(Node from)
        {
            FireOutgoing(from, _ => false);
        }
    }

    private bool EvaluateCondition(FlowCondition condition) => condition switch
    {
        ServiceRunningCondition c =>
            !string.IsNullOrEmpty(c.ServiceName)
            && _services.QueryStatus(c.ServiceName) == ServiceState.Running,
        FileExistsCondition c =>
            !string.IsNullOrEmpty(c.Path) && File.Exists(c.Path),
        ProcessRunningCondition c =>
            !string.IsNullOrEmpty(c.ProcessName)
            && System.Diagnostics.Process.GetProcessesByName(c.ProcessName).Length > 0,
        _ => false,
    };
}
