using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class AgentCliSettingsView : Window
{
    public AgentCliSettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AgentCliSettingsViewModel vm)
        {
            vm.CloseAction = Close;
            // Kick the initial catalog read once the live VM is attached (no-op for the
            // design/render instance — its rows are fixed).
            if (vm.RefreshCommand.CanExecute(null))
                vm.RefreshCommand.Execute(null);
        }
    }
}
