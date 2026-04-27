using System.Diagnostics;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IProcessLauncher
{
    bool TryStart(AppEntry app, string resolvedPath, out string message);

    bool TryStartAndCapture(AppEntry app, string resolvedPath, out Process? process, out string message);
}
