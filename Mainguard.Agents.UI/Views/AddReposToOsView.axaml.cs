using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

/// <summary>The post-setup "Add Repos to Mainguard OS" window (Tools → Add Repos to Mainguard OS…) —
/// the OOBE repo-onboarding flow over the shared <see cref="RepoOnboardingViewModel"/> engine.</summary>
public partial class AddReposToOsView : Window
{
    public AddReposToOsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AddReposToOsViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The titlebar X can fire mid-copy: release the in-flight run so it never outlives its
        // window silently. The engine's cancellation is the same one the Cancel button uses —
        // repositories already copied stay copied.
        if (DataContext is AddReposToOsViewModel vm && vm.IsProvisioningRepos)
        {
            vm.CancelRepoCopyCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
