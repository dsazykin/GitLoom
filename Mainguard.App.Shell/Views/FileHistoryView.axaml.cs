using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

/// <summary>
/// Dedicated file-history dialog (T-12): a rename-following revision list beside the diff of the
/// selected revision against its predecessor, plus a v1 line-history filter. Opened from the staging
/// panel and diff-viewer context menus. History loads when the window opens.
/// </summary>
public partial class FileHistoryView : ChromedWindow
{
    public FileHistoryView()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is FileHistoryViewModel vm)
        {
            await vm.LoadAsync();
        }
    }
}
