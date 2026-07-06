using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ReflogWindow : Window
{
    public ReflogWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ReflogViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
