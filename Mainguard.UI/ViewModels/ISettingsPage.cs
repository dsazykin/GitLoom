namespace Mainguard.UI.ViewModels;

/// <summary>
/// Optional lifecycle hook for a Settings sidebar page's content ViewModel. The Settings window's
/// page-switch logic calls <see cref="OnActivated"/> when a page becomes the visible content and
/// <see cref="OnDeactivated"/> right before switching to a different page — this replaces the
/// Window-level Opened/Closing hooks the migrated standalone dialogs used to rely on (an initial
/// refresh-on-open, disposing a resource, cancelling in-flight work). Pages with no such needs
/// simply don't implement this interface.
/// </summary>
public interface ISettingsPage
{
    void OnActivated();
    void OnDeactivated();
}
