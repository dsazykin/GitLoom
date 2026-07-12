using Avalonia.Controls;

namespace GitLoom.App.Views;

/// <summary>
/// The P2-05 staged-checklist progress window. A pure MVVM surface — the
/// <see cref="ViewModels.BootstrapProgressViewModel"/> drives all state; the view only binds design
/// tokens and component classes.
/// </summary>
public partial class BootstrapProgressView : Window
{
    public BootstrapProgressView()
    {
        InitializeComponent();
    }
}
