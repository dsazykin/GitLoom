using Avalonia.Threading;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Editions;
using Mainguard.UI.Editions;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The Pro subscriber to the tier-1 auto-update outcome seam (<see cref="DaemonAutoRefresh"/>'s
/// <c>onOutcome</c> callback): composes the toast via the pure Core policy
/// (<see cref="DaemonRefreshToast.TryCompose"/> — only Refreshed and RefreshFailed earn one) and posts it
/// onto the UI thread into the shell's toast stack through the <see cref="ProComposition.ShowShellToast"/>
/// seam (step 2f — this Pro-UI assembly must not name the shell's MainWindowViewModel). Quiet by design:
/// no shell present / no toast-worthy outcome both mean simply nothing happens.
/// </summary>
internal static class DaemonUpdateToastPublisher
{
    public static void Publish(DaemonRefreshOutcome outcome)
    {
        if (DaemonRefreshToast.TryCompose(outcome) is not { } toast)
        {
            return; // up-to-date / skipped / unreachable / faulted — no toast, ever
        }

        Dispatcher.UIThread.Post(() => ProComposition.ShowShellToast(toast.Message, toast.IsWarning));
    }
}
