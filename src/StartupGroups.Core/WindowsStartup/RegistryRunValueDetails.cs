namespace StartupGroups.Core.WindowsStartup;

public sealed class RegistryRunValueDetails
{
    public required StartupEntrySource Source { get; init; }

    public required string Name { get; init; }

    public required string Command { get; init; }

    public required RegistryRunValueKind Kind { get; init; }
}
