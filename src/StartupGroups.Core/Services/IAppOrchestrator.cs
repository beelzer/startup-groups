using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IAppOrchestrator
{
    OperationResult LaunchApp(AppEntry app);

    OperationResult StopApp(AppEntry app);

    bool IsRunning(AppEntry app);

    Task<IReadOnlyList<OperationResult>> LaunchGroupAsync(Group group, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationResult>> StopGroupAsync(Group group, CancellationToken cancellationToken = default);
}
