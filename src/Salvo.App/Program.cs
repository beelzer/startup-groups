using System;
using System.IO;
using System.Linq;
using System.Runtime;
using Salvo.App.Services;
using Salvo.Core.Branding;
using Salvo.Core.Services;
using Velopack;

namespace Salvo.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupTimer.Mark("main entered");

        // MultiCore JIT: lets the runtime record which methods get JITted
        // on this run and pre-JIT them in parallel on subsequent launches.
        // Microsoft documents 16-35% cold-start wins for typical desktop
        // apps. Two lines, no risk, runs entirely on background cores.
        // Profile lives at %LocalAppData%\Salvo.UserData\jitprofile.
        TryStartMultiCoreJit();

        // Velopack hooks (--veloapp-install, --veloapp-uninstall, --veloapp-firstrun, etc.)
        // must run BEFORE any WPF/UI initialization. If a hook fires, Run() exits the
        // process; otherwise it returns and we proceed with normal app startup.
        VelopackApp.Build().Run();
        StartupTimer.Mark("velopack hooks done");

        // Single instance: for a tray-resident launcher, second launches are
        // routine (logon task + pinned shortcut + installer "Launch") and
        // would each create their own tray icon and settings/config writer.
        // Wake the existing instance's main window instead. Deliberate
        // relaunches (elevation, post-update restart) wait briefly for the
        // predecessor to exit rather than misreading the handoff as a
        // duplicate launch.
        var isHandoff = args.Any(a =>
            string.Equals(a, AppIdentifiers.SkipElevateRelaunchFlag, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, VelopackUpdateService.RestartedAfterUpdateArg, StringComparison.OrdinalIgnoreCase));
        if (!SingleInstance.TryAcquire(waitForPredecessor: isHandoff))
        {
            return;
        }
        StartupTimer.Mark("single-instance acquired");

        var app = new App();
        StartupTimer.Mark("App ctor done");
        app.InitializeComponent();
        StartupTimer.Mark("App.InitializeComponent done");
        app.Run();
    }

    private static void TryStartMultiCoreJit()
    {
        try
        {
            // AppPaths.LocalDataFolder = %LocalAppData%\Salvo.UserData.
            // Directory creation is idempotent and very fast (sub-ms).
            var profileDir = AppPaths.LocalDataFolder;
            Directory.CreateDirectory(profileDir);
            ProfileOptimization.SetProfileRoot(profileDir);
            ProfileOptimization.StartProfile("startup.jitprofile");
        }
        catch
        {
            // Best-effort; never fail launch over JIT profiling.
        }
    }
}
