using System.Runtime.Versioning;
using System.Security.Principal;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public static class ElevationDetector
{
    private static readonly Lazy<bool> _isElevated = new(ComputeIsElevated);

    public static bool IsElevated => _isElevated.Value;

    private static bool ComputeIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
