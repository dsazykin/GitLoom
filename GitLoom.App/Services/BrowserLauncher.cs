using System.Diagnostics;
using System.Runtime.InteropServices;
using GitLoom.Core.Security;

namespace GitLoom.App.Services;

/// <summary>
/// The single place a URL is handed to the OS default browser. Replaces the
/// per-ViewModel <c>OpenUrlInBrowser</c> copies. Refuses anything that is not an
/// absolute http/https URI (<see cref="SafeWebUrl"/>) — link fields come from host
/// APIs and must never be able to launch a non-web protocol handler or local file.
/// Opening a browser is best-effort; failures never crash the caller.
/// </summary>
public static class BrowserLauncher
{
    public static void OpenUrl(string? url)
    {
        if (!SafeWebUrl.IsHttpOrHttps(url)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url!);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url!);
        }
        catch
        {
            // Best-effort: a missing browser/handler must not take the app down.
        }
    }
}
