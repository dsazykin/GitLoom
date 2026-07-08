using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ReleasesWindow : ChromedWindow
{
    public ReleasesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ReleasesViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Kick the initial list load once the window is up (supported hosts only; the VM no-ops otherwise).
        if (DataContext is ReleasesViewModel vm && vm.IsSupported)
        {
            vm.RefreshListCommand.Execute(null);
        }
    }
}
