namespace StartupGroups.Core.WindowsStartup;

public enum StartupOperationStatus
{
    Ok,
    NeedsAdmin,
    Failed
}

public readonly record struct StartupOperationResult(StartupOperationStatus Status, string Message)
{
    public bool Succeeded => Status == StartupOperationStatus.Ok;

    public static StartupOperationResult Ok(string message) =>
        new(StartupOperationStatus.Ok, message);

    public static StartupOperationResult NeedsAdmin(string message = "Needs admin") =>
        new(StartupOperationStatus.NeedsAdmin, message);

    public static StartupOperationResult Failed(string message) =>
        new(StartupOperationStatus.Failed, message);
}
