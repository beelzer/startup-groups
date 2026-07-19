namespace Salvo.Core.Models;

/// <summary>
/// Show-window hint applied when launching an Executable entry. Passed to
/// the shell as the launch's nShow value: console apps honor it strictly
/// (Hidden means no console window exists to accidentally close), GUI apps
/// treat it as a hint and may still show their own windows.
/// </summary>
public enum LaunchWindowStyle
{
    Normal,
    Minimized,
    Maximized,

    /// <summary>
    /// No window at all. The app still runs and is still detected/stopped
    /// by process matching; readiness falls to the non-window probes
    /// (activity-quiet, input-idle), since a window may never exist.
    /// </summary>
    Hidden,
}
