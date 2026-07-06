using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class AccountsWindow : Window
{
    public AccountsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AccountsViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
