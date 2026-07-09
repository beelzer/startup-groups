using System.IO;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Salvo.App.ViewModels;
using Salvo.Core.WindowsStartup;

namespace Salvo.App.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsStartupIconLoader
{
    public static void LoadFor(IEnumerable<WindowsStartupEntryViewModel> entries)
    {
        var targets = entries
            .Where(e => e.Icon is null)
            .Select(e => (Vm: e, Source: ResolveSource(e.Model)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Source))
            .Select(t => (t.Vm, t.Source!))
            .ToList();

        IconLoad.Start(Dispatcher.CurrentDispatcher, targets, static (vm, icon) => vm.Icon = icon, "StartupIcon-STA");
    }

    private static string? ResolveSource(WindowsStartupEntry entry) => entry.Source switch
    {
        // For folder entries, entry.Command is the full shortcut or exe path;
        // ShellIconExtractor resolves .lnk targets automatically.
        StartupEntrySource.StartupFolderUser or StartupEntrySource.StartupFolderCommon => entry.Command,
        _ => ExtractExecutablePath(entry.Command)
    };

    private static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var trimmed = command.TrimStart();
        string path;
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close <= 1) return null;
            path = trimmed[1..close];
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            path = space < 0 ? trimmed : trimmed[..space];
        }

        var expanded = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(expanded) ? expanded : null;
    }
}
