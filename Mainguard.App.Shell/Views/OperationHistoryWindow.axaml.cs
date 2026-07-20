using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

public partial class OperationHistoryWindow : ChromedWindow
{
    public OperationHistoryWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is OperationHistoryViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
