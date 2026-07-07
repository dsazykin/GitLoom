using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

/// <summary>
/// Dedicated blame dialog (T-33): hosts the T-11 <see cref="BlameView"/> gutter and the T-32
/// "Why this line" PR/issue popover, opened from the staging-panel and diff-viewer "Blame this file"
/// context menus. Blame is turned on and loaded for the pre-set <see cref="BlameViewModel.FilePath"/>
/// when the window opens.
/// </summary>
public partial class BlameWindow : Window
{
    public BlameWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is BlameViewModel vm)
        {
            vm.IsBlameVisible = true;
            await vm.LoadAsync(vm.FilePath);
        }
    }
}
