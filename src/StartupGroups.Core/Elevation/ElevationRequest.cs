using StartupGroups.Core.WindowsStartup;

namespace StartupGroups.Core.Elevation;

public enum ElevationAction
{
    Start,
    Stop,
    WriteRegistryRunValue,
    DeleteRegistryRunValue
}

public sealed class ElevationRequest
{
    public ElevationAction Action { get; set; }

    public List<string> ServiceNames { get; set; } = [];

    // Populated only for WriteRegistryRunValue / DeleteRegistryRunValue.
    public RegistryRunValueEdit? RegistryEdit { get; set; }
}
