using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class RemotesWindow : Window
{
    public RemotesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RemotesViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
