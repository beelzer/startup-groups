namespace StartupGroups.Core.Services;

public enum ServiceState
{
    NotFound,
    Running,
    Stopped,
    Pending
}

public interface IServiceController
{
    ServiceState QueryStatus(string serviceName);

    bool TryStart(string serviceName, TimeSpan timeout, out string message);

    bool TryStop(string serviceName, TimeSpan timeout, out string message);
}
