using WixToolset.BootstrapperApplicationApi;

namespace StartupGroups.Installer.UI;

/// <summary>
/// Burn launches the BA as a separate process (WiX 5 out-of-process model)
/// and pipes us stdin/stdout-style IPC. <see cref="ManagedBootstrapperApplication.Run"/>
/// owns the connect-to-Burn handshake; we instantiate the BA and hand it
/// over. The BA's <see cref="InstallerBootstrapperApplication.Run"/> creates
/// the WPF <see cref="App"/> + dispatcher loop on the BA's STA main thread.
/// </summary>
public static class Program
{
    [System.STAThread]
    public static int Main()
    {
        var ba = new InstallerBootstrapperApplication();
        ManagedBootstrapperApplication.Run(ba);
        return 0;
    }
}
