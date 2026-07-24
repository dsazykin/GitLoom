using Avalonia.Controls;

namespace Mainguard.App.Shell.Views;

/// <summary>Settings → About: app/daemon/OS versions. The initial fetch moved to
/// <c>VersionsViewModel.OnActivated</c>.</summary>
public partial class VersionsView : UserControl
{
    public VersionsView()
    {
        InitializeComponent();
    }
}
