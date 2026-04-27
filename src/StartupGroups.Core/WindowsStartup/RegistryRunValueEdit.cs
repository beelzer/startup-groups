namespace StartupGroups.Core.WindowsStartup;

public sealed class RegistryRunValueEdit
{
    // Parameterless ctor + settable properties so the source-gen JSON serializer
    // can round-trip this through the elevator payload.
    public StartupEntrySource Source { get; set; }

    public string OriginalName { get; set; } = string.Empty;

    public string NewName { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public RegistryRunValueKind Kind { get; set; }
}
