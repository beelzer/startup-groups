using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Salvo.App.Services;

/// <summary>
/// Shared background icon-loading loop. Previously copy-pasted verbatim in
/// AppIconLoader, WindowsStartupIconLoader, and BenchmarksViewModel — only the
/// item type and the assignment lambda differed. Runs on a low-priority
/// background STA thread (shell icon extraction is STA-bound) and marshals each
/// result back through the supplied dispatcher.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconLoad
{
    public static void Start<T>(
        Dispatcher dispatcher,
        IReadOnlyList<(T Vm, string Source)> targets,
        Action<T, BitmapSource> assign,
        string threadName)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            foreach (var (vm, source) in targets)
            {
                try
                {
                    var icon = AppIconCache.Get(source);
                    if (icon is not null)
                    {
                        dispatcher.BeginInvoke(() => assign(vm, icon), DispatcherPriority.Background);
                    }
                }
                catch
                {
                    // Best-effort: a single bad icon must not abort the batch.
                }
            }
        })
        {
            IsBackground = true,
            Name = threadName,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
