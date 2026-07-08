using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ProfilesWindow : ChromedWindow
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
