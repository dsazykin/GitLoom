using Avalonia.Controls;
using Mainguard.App.Shell.ViewModels;

namespace Mainguard.App.Shell.Views;

/// <summary>The SSH Keys content, shared by the standalone <see cref="SshKeysWindow"/> (first-run
/// sign-in, before the Settings window exists) and the Settings "SSH Keys" page (in-session).</summary>
public partial class SshKeysView : UserControl
{
    public SshKeysView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SshKeysViewModel vm)
        {
            vm.CopyToClipboard = text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null && text != null)
                    _ = clipboard.SetTextAsync(text);
            };
        }
    }
}
