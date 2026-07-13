using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GitLoom.App.Views;

/// <summary>The P2-10 Merge Queue Rail (real <c>IMergeQueue</c> binding). See MergeQueueView.axaml.</summary>
public partial class MergeQueueView : UserControl
{
    public MergeQueueView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
