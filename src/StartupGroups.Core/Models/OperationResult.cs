using StartupGroups.Core.Launch;

namespace StartupGroups.Core.Models;

public enum OperationStatus
{
    Succeeded,
    AlreadyInState,
    NotFound,
    NeedsElevation,
    Failed
}

public sealed record OperationResult(
    OperationStatus Status,
    string Message,
    AppEntry? Source = null)
{
    public LaunchMetrics? Metrics { get; init; }

    public bool IsSuccess => Status is OperationStatus.Succeeded or OperationStatus.AlreadyInState;

    public OperationResult WithMetrics(LaunchMetrics? metrics) => this with { Metrics = metrics };

    public static OperationResult Success(string message, AppEntry? source = null) =>
        new(OperationStatus.Succeeded, message, source);

    public static OperationResult AlreadyInState(string message, AppEntry? source = null) =>
        new(OperationStatus.AlreadyInState, message, source);

    public static OperationResult NotFound(string message, AppEntry? source = null) =>
        new(OperationStatus.NotFound, message, source);

    public static OperationResult NeedsElevation(AppEntry? source = null) =>
        new(OperationStatus.NeedsElevation, "Needs admin", source);

    public static OperationResult Failed(string message, AppEntry? source = null) =>
        new(OperationStatus.Failed, message, source);
}
