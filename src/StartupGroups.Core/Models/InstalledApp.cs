namespace StartupGroups.Core.Models;

public enum InstalledAppSource
{
    Desktop,
    Uwp,
    Other,
    Service,
    Scoop
}

public sealed record InstalledApp(
    string Name,
    string Launch,
    string? ExecutablePath,
    string? IconPath,
    InstalledAppSource Source,
    string ParsingName,
    string? Publisher = null,
    string? ServiceName = null);
