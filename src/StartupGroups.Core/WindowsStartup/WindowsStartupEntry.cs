namespace StartupGroups.Core.WindowsStartup;

public enum StartupEntrySource
{
    RegistryRunUser,
    RegistryRunUser32,
    RegistryRunMachine,
    RegistryRunMachine32,
    StartupFolderUser,
    StartupFolderCommon
}

public sealed class WindowsStartupEntry
{
    public required string Name { get; init; }

    public required string Command { get; init; }

    public required StartupEntrySource Source { get; init; }

    public required bool Enabled { get; init; }

    public required bool CanModifyWithoutAdmin { get; init; }

    public string? SourceDescription { get; init; }

    public string SourceShortLabel => Source switch
    {
        StartupEntrySource.RegistryRunUser => "HKCU Run",
        StartupEntrySource.RegistryRunUser32 => "HKCU Run (32-bit)",
        StartupEntrySource.RegistryRunMachine => "HKLM Run",
        StartupEntrySource.RegistryRunMachine32 => "HKLM Run (32-bit)",
        StartupEntrySource.StartupFolderUser => "User startup folder",
        StartupEntrySource.StartupFolderCommon => "Common startup folder",
        _ => "Unknown"
    };
}
