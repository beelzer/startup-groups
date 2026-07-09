using System;
using System.IO;
using System.Runtime;
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
