using System;
using System.Diagnostics;
using System.IO;
using StartupGroups.Core.Branding;

namespace StartupGroups.App.Services;

internal static class ElevationPaths
{
    public static string ResolveElevatorPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var sidecar = Path.Combine(baseDirectory, AppIdentifiers.ElevatorExecutableName);
        if (File.Exists(sidecar))
        {
            return sidecar;
        }

        var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(mainModule))
        {
            var directory = Path.GetDirectoryName(mainModule);
            if (!string.IsNullOrEmpty(directory))
            {
                var next = Path.Combine(directory, AppIdentifiers.ElevatorExecutableName);
                if (File.Exists(next))
                {
                    return next;
                }
            }
        }

        return sidecar;
    }
}
