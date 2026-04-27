using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using StartupGroups.App.Resources;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using Wpf.Ui.Controls;

namespace StartupGroups.App.Services;

public sealed class DialogService : IDialogService
{
    public string? PromptForExecutable(string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.FileDialog_SelectExecutable_Title,
            Filter = Strings.FileDialog_SelectExecutable_Filter,
            CheckFileExists = true
        };

        if (!string.IsNullOrEmpty(initialPath) && System.IO.File.Exists(initialPath))
        {
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(initialPath);
            dialog.FileName = System.IO.Path.GetFileName(initialPath);
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PromptForDirectory(string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Strings.FileDialog_SelectFolder_Title
        };

        if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = GetActiveOwner();
        var messageBox = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = Strings.Action_Yes,
            CloseButtonText = Strings.Action_No,
            PrimaryButtonAppearance = ControlAppearance.Primary
        };

        if (owner is not null)
        {
            messageBox.Owner = owner;
            messageBox.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var result = await messageBox.ShowDialogAsync().ConfigureAwait(true);
        return result == MessageBoxResult.Primary;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var owner = GetActiveOwner();
        var messageBox = new MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = Strings.Action_Ok
        };

        if (owner is not null)
        {
            messageBox.Owner = owner;
            messageBox.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        await messageBox.ShowDialogAsync().ConfigureAwait(true);
    }

    private static Window? GetActiveOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? app.MainWindow;
    }
}
