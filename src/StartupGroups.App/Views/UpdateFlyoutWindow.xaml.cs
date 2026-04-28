using System;
using System.Linq;
using System.Windows;
using StartupGroups.App.ViewModels;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Views;

public partial class UpdateFlyoutWindow : FluentWindow
{
    public UpdateFlyoutWindow(UpdateFlyoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }
}
