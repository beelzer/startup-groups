using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Salvo.Core.Branding;

namespace Salvo.App.Services;

/// <summary>
/// Shared self-relaunch-as-administrator core, previously duplicated (and
/// diverged) between App startup and the settings view-model. Only the shared
/// mechanism lives here — building the runas ProcessStartInfo, starting it, and
/// shutting this instance down. Gating (elevation state, settings) and error
/// handling stay with each caller, which want different behavior.
/// </summary>
internal static class ProcessElevation
{
    /// <summary>
    /// Relaunches the current executable elevated with <paramref name="arguments"/>
    /// and shuts this instance down. Returns false (without starting anything) if
    /// the executable path can't be resolved. Throws <see cref="System.ComponentModel.Win32Exception"/>
    /// if the elevated start fails — native code 1223 means the user declined the
    /// UAC prompt; callers decide how to react.
    /// </summary>
    public static bool RelaunchSelfAsAdmin(string arguments)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        // Always mark the child as a deliberate relaunch: it must skip the
        // AlwaysRunAsAdmin auto-elevate check (no relaunch loop) and wait for
        // this instance's single-instance mutex instead of treating the
        // handoff as a duplicate launch.
        if (!arguments.Contains(AppIdentifiers.SkipElevateRelaunchFlag, StringComparison.OrdinalIgnoreCase))
        {
            arguments = string.IsNullOrWhiteSpace(arguments)
                ? AppIdentifiers.SkipElevateRelaunchFlag
                : arguments + " " + AppIdentifiers.SkipElevateRelaunchFlag;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };
        Process.Start(psi);
        Application.Current.Shutdown();
        return true;
    }
}
