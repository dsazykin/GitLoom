using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

/// <summary>The tier-2 in-place VM upgrade offer/progress dialog (non-modal; the
/// <see cref="AgentCliSettingsView"/> window pattern). Closing is refused while the upgrade runs —
/// abandoning a distro replacement mid-flight has no safe half-state to return to.</summary>
public partial class VmUpgradeOfferWindow : Window
{
    public VmUpgradeOfferWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (DataContext is VmUpgradeOfferViewModel { IsRunning: true })
                e.Cancel = true;
        };
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is VmUpgradeOfferViewModel vm)
            vm.CloseAction = Close;
    }
}
