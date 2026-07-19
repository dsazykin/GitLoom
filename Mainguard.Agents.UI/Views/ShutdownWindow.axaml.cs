using Avalonia.Controls;

namespace GitLoom.App.Views;

/// <summary>
/// The small shutdown window (owner design, 2026-07-17): a changing status line while the app's
/// existing exit teardown runs (release the VM keep-alive; when StopVmOnExit is on, stop GitLoom OS).
/// Non-resizable and chromeless — it appears only during the final full-exit teardown and closes
/// with the process.
/// </summary>
public partial class ShutdownWindow : Window
{
    public ShutdownWindow()
    {
        InitializeComponent();
    }
}
