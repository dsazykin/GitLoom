using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class InteractiveRebaseWindow : ChromedWindow
{
    public InteractiveRebaseWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is InteractiveRebaseViewModel vm)
        {
            vm.RequestClose = Close;
        }
    }
}
