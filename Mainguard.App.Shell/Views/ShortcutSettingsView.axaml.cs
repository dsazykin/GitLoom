using Avalonia.Controls;

namespace Mainguard.App.Shell.Views;

/// <summary>Settings → Keyboard Shortcuts, embedded as a page (was a standalone dialog). Save/Cancel
/// still persist/discard pending rebinds as before; <c>RequestClose</c> (which used to close the
/// window) is simply left unsubscribed here — there's nothing to close.</summary>
public partial class ShortcutSettingsView : UserControl
{
    public ShortcutSettingsView()
    {
        InitializeComponent();
    }
}
