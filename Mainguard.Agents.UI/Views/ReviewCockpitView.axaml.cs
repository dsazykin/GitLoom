using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GitLoom.App.Views;

/// <summary>The P2-11 Review Cockpit (ControlCenterDesign §6). See ReviewCockpitView.axaml. No rule logic here.</summary>
public partial class ReviewCockpitView : UserControl
{
    public ReviewCockpitView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
