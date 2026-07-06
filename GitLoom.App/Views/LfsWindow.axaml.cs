using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class LfsWindow : Window
{
    public LfsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LfsViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
