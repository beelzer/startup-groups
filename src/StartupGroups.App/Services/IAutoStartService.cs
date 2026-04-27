namespace StartupGroups.App.Services;

public interface IAutoStartService
{
    bool IsEnabled();

    bool IsEnabledElevated();

    void Enable(bool runElevated = false);

    void Disable();
}
