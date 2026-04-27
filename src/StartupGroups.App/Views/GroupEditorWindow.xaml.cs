using System.Linq;
using System.Windows;
using StartupGroups.App.ViewModels;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Views;

public partial class GroupEditorWindow : FluentWindow
{
    public GroupEditorWindow(GroupEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
