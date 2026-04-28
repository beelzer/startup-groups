using WixToolset.BootstrapperApplicationApi;

namespace StartupGroups.Installer.UI;

/// <summary>
/// Burn launches the BA as a separate process (WiX 5 out-of-process model)
/// and pipes us stdin/stdout-style IPC. <see cref="ManagedBootstrapperApplication.Run"/>
/// owns the connect-to-Burn handshake; we just instantiate the BA and hand
/// it over.
/// </summary>
public static class Program
{
    [System.STAThread]
    public static int Main()
    {
        var app = new InstallerBootstrapperApplication();
        ManagedBootstrapperApplication.Run(app);
        return 0;
    }
}
