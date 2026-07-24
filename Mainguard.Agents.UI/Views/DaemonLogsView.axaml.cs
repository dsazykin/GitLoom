using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

/// <summary>Settings → Daemon Logs, embedded as a page (was a standalone dialog). The initial
/// read-kick and disposal moved to <c>DaemonLogsViewModel.OnActivated</c>/<c>OnDeactivated</c>.</summary>
public partial class DaemonLogsView : UserControl
{
    public DaemonLogsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DaemonLogsViewModel vm)
        {
            // The clipboard lives on the TopLevel, not the ViewModel — wire it so Copy stays display-free.
            vm.CopyAction = text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    _ = clipboard.SetTextAsync(text);
            };
        }
    }
}
