using System;
using System.Linq;
using GitLoom.Core.Models;
using GitLoom.Core.Security;

namespace GitLoom.Core.Services;

/// <summary>An owner/repo pair parsed once from the origin remote, passed to a host provider (T-23 PRs, T-24 issues).</summary>
internal readonly record struct RepoSlug(string Owner, string Name);

/// <summary>
/// Shared origin-host + token + owner/repo resolution for the host-integration services (T-23 pull
/// requests, T-24 issues). Extracted so both services run through <b>one</b> path — a second
/// copy-pasted host/token resolver is a rejection trigger. Per-host provider dispatch (which differs
/// between PRs and issues) stays in each service; this owns only the generic host/token/slug plumbing.
///
/// <para>SECURITY (G-4): the token is read from the keyring and returned to the caller, which hands it
/// to a provider's <c>Authorization</c> header only — this class never puts it in a URL, argv, or log.</para>
/// </summary>
internal sealed class HostConnectionResolver
{
    private readonly IGitService _git;
    private readonly ISecureKeyring _keyring;

    public HostConnectionResolver(IGitService git, ISecureKeyring keyring)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _keyring = keyring ?? throw new ArgumentNullException(nameof(keyring));
    }

    /// <summary>
    /// Reads the origin (else default, else sole) remote and classifies its host. Returns false — never
    /// throws — when the remotes can't be read or none point at a recognizable host, so a caller's
    /// <c>IsSupported</c> probe degrades gracefully instead of surfacing an error.
    /// </summary>
    public bool TryResolveHost(string repoPath, out string host, out HostKind kind, out string remoteUrl)
    {
        host = ""; kind = HostKind.Unknown; remoteUrl = "";
        try
        {
            var remotes = _git.GetRemotes(repoPath);
            var remote = remotes.FirstOrDefault(r => string.Equals(r.Name, "origin", StringComparison.Ordinal))
                         ?? remotes.FirstOrDefault();
            if (remote is null || string.IsNullOrWhiteSpace(remote.FetchUrl)) return false;

            remoteUrl = remote.FetchUrl;
            (host, kind) = GitHostDetector.Detect(remoteUrl);
            return !string.IsNullOrEmpty(host);
        }
        catch
        {
            // A repo whose remotes can't be read is simply "unsupported" — never throws from IsSupported.
            return false;
        }
    }

    /// <summary>The stored token for a host (keyring key <c>token_&lt;host&gt;</c>), or null when none is set.</summary>
    public string? TokenFor(string host) => _keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost(host));

    /// <summary>Parses the <c>owner</c>/<c>repo</c> slug from a remote URL, or null when it isn't a clear repo path.</summary>
    public static RepoSlug? ParseSlug(string remoteUrl)
    {
        var parsed = GitHostDetector.ParseOwnerRepo(remoteUrl);
        return parsed is null ? null : new RepoSlug(parsed.Value.Owner, parsed.Value.Repo);
    }
}
