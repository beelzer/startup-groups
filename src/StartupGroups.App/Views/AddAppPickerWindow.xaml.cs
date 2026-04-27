using System.Linq;
using System.Windows;
using StartupGroups.App.ViewModels;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Views;

public partial class AddAppPickerWindow : FluentWindow
{
    private readonly AddAppPickerViewModel _viewModel;

    public AddAppPickerWindow(AddAppPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

        _viewModel.RequestClose += OnRequestClose;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnRequestClose(object? sender, bool result)
    {
        DialogResult = result;
        Close();
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelCommand.Execute(null);
    }
}
