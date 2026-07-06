using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GitLoom.App.Controls;

// Code-behind for the T-13 image-diff control. Intentionally thin: all state lives on
// ImageDiffViewModel. TODO(T-13 human-review): image-diff swipe control feel — wire the swipe /
// onion-skin overlay gesture here once the interaction design is signed off.
public partial class ImageDiffControl : UserControl
{
    public ImageDiffControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
