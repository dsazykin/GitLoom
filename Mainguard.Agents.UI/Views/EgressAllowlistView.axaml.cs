using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class EgressAllowlistView : Window
{
    public EgressAllowlistView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EgressAllowlistViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
