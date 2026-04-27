using System;
using Velopack;

namespace StartupGroups.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack hooks (--veloapp-install, --veloapp-uninstall, --veloapp-firstrun, etc.)
        // must run BEFORE any WPF/UI initialization. If a hook fires, Run() exits the
        // process; otherwise it returns and we proceed with normal app startup.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
