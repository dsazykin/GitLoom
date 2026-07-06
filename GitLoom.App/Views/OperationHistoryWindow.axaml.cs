using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class OperationHistoryWindow : Window
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
