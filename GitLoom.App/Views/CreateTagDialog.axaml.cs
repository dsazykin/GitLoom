using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class CreateTagDialog : Window
{
    public CreateTagDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CreateTagDialogViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
