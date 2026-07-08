using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class NotificationsWindow : ChromedWindow
{
    public NotificationsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is NotificationsViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Kick the initial load once the window is up (supported hosts only; the VM no-ops otherwise).
        if (DataContext is NotificationsViewModel vm && vm.IsSupported)
        {
            vm.RefreshCommand.Execute(null);
        }
    }
}
