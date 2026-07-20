using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

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
