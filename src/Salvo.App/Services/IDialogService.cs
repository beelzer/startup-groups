using System.Threading.Tasks;

namespace Salvo.App.Services;

public interface IDialogService
{
    string? PromptForExecutable(string? initialPath = null);

    string? PromptForDirectory(string? initialPath = null);

    Task<bool> ConfirmAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);
}
