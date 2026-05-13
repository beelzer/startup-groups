namespace Salvo.Core.Launch;

public sealed record ReadinessResult(
    LaunchOutcome Outcome,
    ReadinessSignal Signal,
    DateTimeOffset ResolvedAt);
