using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using Mainguard.Agents.Agents.Bootstrap;

namespace GitLoom.App.Services;

/// <summary>
/// The App's subscriber to the tier-1 auto-update outcome seam (<see cref="DaemonAutoRefresh"/>'s
/// <c>onOutcome</c> callback): composes the toast via the pure Core policy
/// (<see cref="DaemonRefreshToast.TryCompose"/> — only Refreshed and RefreshFailed earn one) and
/// posts it onto the UI thread into the main window shell's toast stack. Quiet by design: no main
/// window / no MainWindowViewModel / no toast-worthy outcome all mean simply nothing happens.
/// </summary>
internal static class DaemonUpdateToastPublisher
{
    public static void Publish(DaemonRefreshOutcome outcome)
    {
        if (DaemonRefreshToast.TryCompose(outcome) is not { } toast)
        {
            return; // up-to-date / skipped / unreachable / faulted — no toast, ever
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
            {
                shell.ShowToast(toast.Message, isError: toast.IsWarning);
            }
        });
    }
}
