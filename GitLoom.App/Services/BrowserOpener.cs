using GitLoom.Core.Security;

namespace GitLoom.App.Services;

/// <summary>
/// The App's <see cref="IBrowserOpener"/> — a thin adapter over the single <see cref="BrowserLauncher"/>
/// so <see cref="LoopbackOAuthListener"/> opens the authorize URL through the ONE scheme-validated
/// launcher (no second browser-launch path). Core stays UI-free; only the App knows how to reach the
/// OS browser.
/// </summary>
public sealed class BrowserOpener : IBrowserOpener
{
    public void Open(string url) => BrowserLauncher.OpenUrl(url);
}
