using Avalonia.Controls;

namespace Mainguard.App.Shell.Views;

/// <summary>The Accounts content, shared by the standalone <see cref="AccountsWindow"/> (first-run
/// sign-in, before the Settings window exists) and the Settings "Accounts" page (in-session).</summary>
public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
    }
}
