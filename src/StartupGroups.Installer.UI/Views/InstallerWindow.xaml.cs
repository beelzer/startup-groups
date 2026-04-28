using StartupGroups.Installer.UI.ViewModels;
using Wpf.Ui.Controls;

namespace StartupGroups.Installer.UI.Views;

public partial class InstallerWindow : FluentWindow
{
    public InstallerWindow(InstallerWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
