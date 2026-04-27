namespace StartupGroups.Core.Launch;

public sealed record ReadinessResult(
    LaunchOutcome Outcome,
    ReadinessSignal Signal,
    DateTimeOffset ResolvedAt);
