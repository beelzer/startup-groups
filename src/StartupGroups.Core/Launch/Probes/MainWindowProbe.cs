using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Models;
using StartupGroups.Core.Native;
using StartupGroups.Core.Services;
using static StartupGroups.Core.Native.UserInterop;

namespace StartupGroups.Core.Launch.Probes;

[SupportedOSPlatform("windows")]
public sealed class MainWindowProbe : IReadinessProbe
{
    private static readonly TimeSpan PollInterval = Timeouts.ProbePollDefault;

    public ReadinessSignal Signal => ReadinessSignal.MainWindowVisible;

    public bool AppliesTo(ProbeContext context) => context.App.Kind != AppKind.Service;

    public async Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryFindReadyWindow(context, out var window))
            {
                context.Session.TryMarkMainWindow(DateTimeOffset.UtcNow);
                context.Logger.LogDebug("MainWindowProbe fired: hwnd={Hwnd} pid={Pid}", window.HWnd, window.Pid);
                return true;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryFindReadyWindow(ProbeContext context, out (IntPtr HWnd, int Pid) result)
    {
        result = default;
        var pids = context.Session.EnumerateDescendantPids();
        if (pids.Count == 0)
        {
            return false;
        }

        var pidSet = new HashSet<int>(pids);
        IntPtr match = IntPtr.Zero;
        int matchPid = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pidSet.Contains((int)pid)) return true;
            if (GetWindowTextLengthW(hwnd) <= 0) return true;

            var exStyle = (uint)(GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64() & uint.MaxValue);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            if (IsHungAppWindow(hwnd)) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (SendMessageTimeoutW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _) == IntPtr.Zero)
            {
                return true;
            }

            match = hwnd;
            matchPid = (int)pid;
            return false;
        }, IntPtr.Zero);

        if (match != IntPtr.Zero)
        {
            result = (match, matchPid);
            return true;
        }
        return false;
    }
}
