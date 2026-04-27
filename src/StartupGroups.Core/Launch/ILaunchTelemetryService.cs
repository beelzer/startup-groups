using System.Diagnostics;
using System.Runtime.Versioning;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public interface ILaunchTelemetryService
{
    event EventHandler<LaunchMetrics>? MetricsSaved;

    Task<LaunchMetrics> BeginObservation(AppEntry app, string? resolvedPath, string? groupId, Process? process);
}
