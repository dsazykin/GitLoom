using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

public partial class DaemonLogsView : Window
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
            vm.CloseAction = Close;
            // The clipboard lives on the TopLevel, not the ViewModel — wire it so Copy stays display-free.
            vm.CopyAction = text =>
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    _ = clipboard.SetTextAsync(text);
            };

            // Kick the initial read once the live VM is attached (no-op for the design/render instance).
            if (vm.RefreshCommand.CanExecute(null))
                vm.RefreshCommand.Execute(null);
        }
    }
}
