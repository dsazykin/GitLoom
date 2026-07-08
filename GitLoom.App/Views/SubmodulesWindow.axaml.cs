using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class SubmodulesWindow : ChromedWindow
{
    public SubmodulesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SubmodulesViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
