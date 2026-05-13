using System.Windows.Controls;
using Salvo.App.Controls;
using Salvo.App.Resources;
using Salvo.App.ViewModels;

namespace Salvo.App.Views;

internal static class TrayMenuFactory
{
    public static ContextMenu Build(TrayViewModel viewModel)
    {
        var menu = new ContextMenu();

        foreach (var group in viewModel.TrayGroups)
        {
            var header = new MenuItem
            {
                Header = group.Name,
                Icon = new GroupIconView
                {
                    Icon = group.Model.Icon,
                    IconSize = 16,
                }
            };

            header.Items.Add(new MenuItem
            {
                Header = Strings.Action_LaunchAll,
                Command = group.LaunchCommand
            });
            header.Items.Add(new MenuItem
            {
                Header = Strings.Action_StopAll,
                Command = group.StopCommand
            });

            menu.Items.Add(header);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem
        {
            Header = Strings.Tray_OpenMain,
            Command = viewModel.ShowMainWindowCommand
        });
        menu.Items.Add(new MenuItem
        {
            Header = Strings.Tray_Exit,
            Command = viewModel.ExitCommand
        });

        return menu;
    }
}
