using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Dock.Avalonia.Controls;

namespace GitLoom.App.Views.Agents;

public partial class AgentWorkspaceView : UserControl
{
    private DockControl? _dock;

    public AgentWorkspaceView() => InitializeComponent();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _dock = this.FindControl<DockControl>("Dock");
    }

    // Mitigate the documented Dock.Avalonia floating-window / visual-tree leak: when this view
    // leaves the tree (agent tab closed / workspace swapped), drop the DockControl's Layout so it
    // releases the panes it built and unregisters from the factory. Without this, the DockControl's
    // rendered visual graph is retained per open/close (P2-13 invariant #1).
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_dock is not null)
            _dock.Layout = null;
    }
}
