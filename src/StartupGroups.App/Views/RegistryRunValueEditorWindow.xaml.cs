using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StartupGroups.App.ViewModels;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace StartupGroups.App.Views;

public partial class RegistryRunValueEditorWindow : FluentWindow
{
    private readonly RegistryRunValueEditorViewModel _viewModel;

    public RegistryRunValueEditorWindow(RegistryRunValueEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ChipEditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.IsVisible)
        {
            FocusChipEditor(textBox);
        }
    }

    private void ChipEditBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox
            && textBox.IsVisible
            && textBox.DataContext is ArgumentChipViewModel chip
            && chip.IsEditing)
        {
            FocusChipEditor(textBox);
        }
    }

    private static void FocusChipEditor(TextBox textBox)
    {
        textBox.Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            Keyboard.Focus(textBox);
            textBox.CaretIndex = textBox.Text.Length;
        }, DispatcherPriority.ContextIdle);
    }

    private void ChipEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox
            && textBox.DataContext is ArgumentChipViewModel chip
            && chip.IsEditing)
        {
            chip.CommitEditCommand.Execute(null);
        }
    }
}
