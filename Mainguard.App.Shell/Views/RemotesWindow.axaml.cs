using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

public partial class RemotesWindow : ChromedWindow
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
