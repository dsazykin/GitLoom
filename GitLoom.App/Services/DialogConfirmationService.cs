using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Services;

/// <summary>
/// Production <see cref="IConfirmationService"/> — shows the shared <see cref="Views.ConfirmationDialog"/>
/// modally over the main window. When there is no desktop lifetime / main window (e.g. headless render
/// tests), it declines the action rather than throwing, so a destructive command can never run unconfirmed.
/// </summary>
public sealed class DialogConfirmationService : IConfirmationService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmButtonText)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return false;
        }

        var vm = new ConfirmationDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmButtonText = confirmButtonText
        };
        var dialog = new Views.ConfirmationDialog { DataContext = vm };
        await dialog.ShowDialog(desktop.MainWindow);
        return vm.IsConfirmed;
    }
}
