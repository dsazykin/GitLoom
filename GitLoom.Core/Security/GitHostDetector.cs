using System;
using GitLoom.Core.Models;

namespace GitLoom.Core.Security;

/// <summary>
/// Detects the Git hosting provider from a remote URL and knows the username
/// convention each host expects for token authentication. Kept separate from
/// GitService so it can be unit-tested and reused by the future multi-host
/// auth / SSH key manager (Category 2.8).
/// </summary>
public static class GitHostDetector
{
    public static (string Host, HostKind Kind) Detect(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return (string.Empty, HostKind.Unknown);

        string host = "";
        // scp-like syntax: git@host:path (no scheme, but has a ':').
        if (remoteUrl.StartsWith("git@", StringComparison.Ordinal) ||
            (!remoteUrl.Contains("://") && remoteUrl.Contains(':')))
        {
            var at = remoteUrl.IndexOf('@');
            var start = at >= 0 ? at + 1 : 0;
            var colon = remoteUrl.IndexOf(':', start);
            if (colon > start) host = remoteUrl.Substring(start, colon - start);
        }
        else if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }

        var lower = host.ToLowerInvariant();
        var kind = lower switch
        {
            "github.com" => HostKind.GitHub,
            "gitlab.com" => HostKind.GitLab,
            "bitbucket.org" => HostKind.Bitbucket,
            _ when lower.Contains("dev.azure.com") || lower.Contains("visualstudio.com") => HostKind.AzureDevOps,
            _ => HostKind.Unknown
        };
        return (host, kind);
    }

    /// <summary>Username each host expects when authenticating with a token.</summary>
    public static string UsernameForToken(HostKind kind) => kind switch
    {
        HostKind.GitHub => "x-access-token",
        HostKind.GitLab => "oauth2",
        HostKind.Bitbucket => "x-token-auth",
        HostKind.AzureDevOps => "token",
        _ => "x-access-token"
    };

    /// <summary>Keyring key under which a host's token is stored.</summary>
    public static string TokenKeyForHost(string host) => $"token_{host}";
}
