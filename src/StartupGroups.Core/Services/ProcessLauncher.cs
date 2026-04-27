using System.Diagnostics;
using System.Runtime.Versioning;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class ProcessLauncher : IProcessLauncher
{
    public bool TryStart(AppEntry app, string resolvedPath, out string message)
    {
        var ok = TryStartAndCapture(app, resolvedPath, out var process, out message);
        process?.Dispose();
        return ok;
    }

    public bool TryStartAndCapture(AppEntry app, string resolvedPath, out Process? process, out string message)
    {
        process = null;

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedPath,
            UseShellExecute = true,
            WorkingDirectory = ResolveWorkingDirectory(app, resolvedPath)
        };

        if (!string.IsNullOrWhiteSpace(app.Args))
        {
            startInfo.Arguments = app.Args;
        }

        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                message = "Launched";
                return true;
            }

            message = "Launched";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static string ResolveWorkingDirectory(AppEntry app, string resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(app.WorkingDirectory))
        {
            var expanded = Environment.ExpandEnvironmentVariables(app.WorkingDirectory);
            if (Directory.Exists(expanded))
            {
                return expanded;
            }
        }

        if (resolvedPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.CurrentDirectory;
        }

        return Path.GetDirectoryName(resolvedPath) ?? Environment.CurrentDirectory;
    }
}
