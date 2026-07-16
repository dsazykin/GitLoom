using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class WorktreeWindow : ChromedWindow
{
    public WorktreeWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is WorktreePanelViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
