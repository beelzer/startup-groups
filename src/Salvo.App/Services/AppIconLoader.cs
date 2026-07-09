using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Salvo.App.ViewModels;
using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.App.Services;

[SupportedOSPlatform("windows")]
internal static class AppIconLoader
{
    public static void LoadFor(IEnumerable<AppEntryViewModel> apps)
    {
        var targets = apps
            .Where(a => a.Icon is null)
            .Select(a => (Vm: a, Source: ResolveSource(a)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Source))
            .Select(t => (t.Vm, t.Source!))
            .ToList();

        IconLoad.Start(Dispatcher.CurrentDispatcher, targets, static (vm, icon) => vm.Icon = icon, "AppIcon-STA");
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
