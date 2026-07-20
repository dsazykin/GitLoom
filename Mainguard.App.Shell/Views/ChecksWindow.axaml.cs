using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

public partial class ChecksWindow : ChromedWindow
{
    public ChecksWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ChecksViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Kick the initial checks load once the window is up (supported hosts only; the VM no-ops otherwise).
        if (DataContext is ChecksViewModel vm && vm.IsSupported)
        {
            vm.RefreshCommand.Execute(null);
        }
    }
}
