namespace StartupGroups.Core.WindowsStartup;

public interface IWindowsStartupService
{
    IReadOnlyList<WindowsStartupEntry> Enumerate();

    StartupOperationResult TrySetEnabled(WindowsStartupEntry entry, bool enabled);

    StartupOperationResult TryRemove(WindowsStartupEntry entry);

    StartupOperationResult TryAddUserRunEntry(string name, string command);

    RegistryRunValueDetails? TryReadRunValue(WindowsStartupEntry entry);

    StartupOperationResult TryUpdateRunValue(RegistryRunValueEdit edit);

    IReadOnlyList<string> GetSiblingValueNames(StartupEntrySource source);
}
