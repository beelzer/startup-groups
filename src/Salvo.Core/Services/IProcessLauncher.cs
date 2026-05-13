using System.Diagnostics;
using Salvo.Core.Models;

namespace Salvo.Core.Services;

public interface IProcessLauncher
{
    bool TryStart(AppEntry app, string resolvedPath, out string message);

    bool TryStartAndCapture(AppEntry app, string resolvedPath, out Process? process, out string message);
}
