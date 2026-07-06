using System;

namespace GitLoom.Core.Security;

/// <summary>
/// Guard for URLs that are about to be handed to the OS shell / default browser.
/// Host-provided link fields (a PR's <c>html_url</c>, a notification target, a check
/// run's details link) are external data: launching them through the shell without a
/// scheme check would let a non-web URI (<c>file:</c>, <c>\\share</c>, custom
/// protocol handlers) start an arbitrary local program. Only absolute http/https
/// URIs may be opened.
/// </summary>
public static class SafeWebUrl
{
    public static bool IsHttpOrHttps(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
