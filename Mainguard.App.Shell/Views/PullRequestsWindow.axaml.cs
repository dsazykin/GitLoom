using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

public partial class PullRequestsWindow : ChromedWindow
{
    public PullRequestsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PullRequestsViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Kick the initial list load once the window is up (supported hosts only; the VM no-ops otherwise).
        if (DataContext is PullRequestsViewModel vm && vm.IsSupported)
        {
            vm.RefreshListCommand.Execute(null);
        }
    }
}
