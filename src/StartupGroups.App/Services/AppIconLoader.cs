using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Threading;
using StartupGroups.App.ViewModels;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.App.Services;

[SupportedOSPlatform("windows")]
internal static class AppIconLoader
{
    public static void LoadFor(IEnumerable<AppEntryViewModel> apps)
    {
        var targets = apps
            .Where(a => a.Icon is null)
            .Select(a => (vm: a, source: ResolveSource(a)))
            .Where(t => !string.IsNullOrWhiteSpace(t.source))
            .ToList();

        if (targets.Count == 0) return;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() =>
        {
            foreach (var (vm, source) in targets)
            {
                try
                {
                    var icon = AppIconCache.Get(source!);
                    if (icon is not null)
                    {
                        dispatcher.BeginInvoke(() => vm.Icon = icon, DispatcherPriority.Background);
                    }
                }
                catch
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "AppIcon-STA",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static string? ResolveSource(AppEntryViewModel vm)
    {
        if (vm.Kind == AppKind.Executable && !string.IsNullOrWhiteSpace(vm.Path))
        {
            return vm.Path;
        }

        if (vm.Kind == AppKind.Service && !string.IsNullOrWhiteSpace(vm.Service))
        {
            return WindowsServicesProvider.TryResolveImagePath(vm.Service!);
        }

        return null;
    }
}
