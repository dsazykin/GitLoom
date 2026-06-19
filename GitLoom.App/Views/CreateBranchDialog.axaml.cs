using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class CreateBranchDialog : Window
{
    public CreateBranchDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CreateBranchDialogViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
