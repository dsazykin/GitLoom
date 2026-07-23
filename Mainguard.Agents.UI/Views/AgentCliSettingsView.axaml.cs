using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

/// <summary>Settings → Agent CLIs, embedded as a page (was a standalone dialog). The initial
/// catalog-read kick moved to <c>AgentCliSettingsViewModel.OnActivated</c>.</summary>
public partial class AgentCliSettingsView : UserControl
{
    public AgentCliSettingsView()
    {
        InitializeComponent();
    }
}
