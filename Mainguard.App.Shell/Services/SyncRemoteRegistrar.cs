using System;
using System.Linq;
using Mainguard.Git.Services;

namespace GitLoom.App.Services;

/// <summary>
/// Idempotently registers the daemon-owned sync remote on the host repo (P2-06 Windows side).
/// The remote's <b>name and URL come verbatim from the daemon's <c>ProvisionRepo</c> response</c>
/// (fields <c>sync_remote_name</c> / <c>sync_remote_url</c>) — never a hardcoded literal, so the
/// App stays substrate-agnostic (ESC-I2: the App speaks only gRPC + <see cref="IGitService"/> and
/// never references the daemon substrate facade). Extracted from the view-model so the run-twice →
/// one-remote idempotence is unit-testable.
/// </summary>
public sealed class SyncRemoteRegistrar
{
    private readonly IGitService _git;

    public SyncRemoteRegistrar(IGitService git)
    {
        _git = git;
    }

    /// <summary>
    /// Ensures a remote named <paramref name="remoteName"/> points at <paramref name="remoteUrl"/>:
    /// skips when it already matches, updates the URL when it changed, adds it when absent.
    /// </summary>
    public void Register(string repoPath, string remoteName, string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteName) || string.IsNullOrWhiteSpace(remoteUrl))
        {
            return;
        }

        var existing = _git.GetRemotes(repoPath).FirstOrDefault(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
        if (existing is null)
        {
            _git.AddRemote(repoPath, remoteName, remoteUrl);
            return;
        }

        if (!string.Equals(existing.FetchUrl, remoteUrl, StringComparison.Ordinal))
        {
            _git.SetRemoteUrl(repoPath, remoteName, remoteUrl);
        }

        // Already present with the same URL: idempotent no-op.
    }
}
