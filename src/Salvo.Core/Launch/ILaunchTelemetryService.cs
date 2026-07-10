using System.Diagnostics;
using System.Runtime.Versioning;
using Salvo.Core.Models;

namespace Salvo.Core.Launch;

[SupportedOSPlatform("windows")]
public interface ILaunchTelemetryService
{
    event EventHandler<LaunchMetrics>? MetricsSaved;

    /// <summary>
    /// Start observing a launch in the background and return the task that
    /// completes with the saved metrics. Ownership of
    /// <paramref name="process"/> transfers to the service: it is disposed
    /// once the root has been attached, so the caller must not touch it
    /// afterwards (and must dispose it itself only when it never calls
    /// this method).
    /// </summary>
    Task<LaunchMetrics> BeginObservation(AppEntry app, string? resolvedPath, string? groupId, Process? process);
}
