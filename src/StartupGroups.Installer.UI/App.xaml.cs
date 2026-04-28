using System.Windows;

namespace StartupGroups.Installer.UI;

/// <summary>
/// Hosts App.xaml's merged resource dictionaries (WPF-UI themes, control
/// styles). We don't auto-start this Application via the SDK — Burn invokes
/// the BA via <see cref="WixToolset.BootstrapperApplicationApi.ManagedBootstrapperApplication.Run"/>
/// which calls back into <see cref="InstallerBootstrapperApplication.Run"/>.
/// That method <c>new</c>'s this class, calls <c>InitializeComponent</c> to
/// load the XAML, then drives <see cref="System.Windows.Threading.Dispatcher.Run"/>
/// directly — so <see cref="OnStartup"/> never fires. Theme application
/// happens explicitly in the BA after <c>InitializeComponent</c>.
/// </summary>
public partial class App : Application;
