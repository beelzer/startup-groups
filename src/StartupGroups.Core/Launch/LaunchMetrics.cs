namespace StartupGroups.Core.Launch;

public sealed record LaunchMetrics
{
    public required Guid LaunchId { get; init; }
    public required string AppId { get; init; }
    public string? AppName { get; init; }
    public string? GroupId { get; init; }
    public string? ResolvedPath { get; init; }
    public string? ResolvedPathHash { get; init; }
    public int? RootPid { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? ProcessStartReturnedAt { get; init; }
    public DateTimeOffset? PidResolvedAt { get; init; }
    public DateTimeOffset? MainWindowAt { get; init; }
    public DateTimeOffset? InputIdleAt { get; init; }
    public DateTimeOffset? QuietAt { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }
    public required LaunchOutcome Outcome { get; init; }
    public required ReadinessSignal SignalFired { get; init; }
    public required bool IsCold { get; init; }
    public required DateTimeOffset BootEpochUtc { get; init; }
    public string? AppVersion { get; init; }
    public string? Notes { get; init; }

    public TimeSpan? TotalDuration => ReadyAt is null ? null : ReadyAt - RequestedAt;
}
