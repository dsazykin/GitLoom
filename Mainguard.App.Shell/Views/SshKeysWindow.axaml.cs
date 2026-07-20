using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class SshKeysWindow : ChromedWindow
{
    public SshKeysWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SshKeysViewModel vm)
        {
            vm.CloseAction = Close;
            vm.CopyToClipboard = text =>
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard != null && text != null)
                    _ = clipboard.SetTextAsync(text);
            };
        }
    }
}
