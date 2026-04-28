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
    // No [STAThread] here. mbanative initialises COM as MTA inside
    // BootstrapperApplicationRun; an STA Main throws RPC_E_CHANGED_MODE
    // (0x80010106 — "Cannot change thread mode after it is set"). WPF
    // still needs STA, so the BA spawns a dedicated STA UI thread inside
    // its Run() override.
    public static int Main()
    {
        var ba = new InstallerBootstrapperApplication();
        ManagedBootstrapperApplication.Run(ba);
        return 0;
    }
}
