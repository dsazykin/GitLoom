using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ProfilesWindow : Window
{
    public ProfilesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ProfilesViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
