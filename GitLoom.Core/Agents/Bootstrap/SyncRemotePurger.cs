using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mainguard.Git.Exceptions;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>The outcome of a <see cref="SyncRemotePurger"/> run: how many repos had the remote removed
/// versus tolerated (missing repo / no such remote). <see cref="RemovedFrom"/> lists the repo paths the
/// remote was actually stripped from.</summary>
public sealed record SyncRemotePurgeReport(int Removed, int Tolerated, IReadOnlyList<string> RemovedFrom);

/// <summary>
/// The optional, default-OFF uninstall step (P2-22 §J-6 / Q2): strip the ONE quarantine sync remote from
/// every known repo. It never touches a working tree — only the added remote is removed. Decoupled from
/// <c>GitService</c> and the DB by injection: it takes the resolved repo paths, the substrate-resolved
/// remote name (SC-2 — never a hardcoded <c>"gitloom-vm"</c>), and a <c>(repoPath, remoteName)</c> remove
/// action. It is failure-tolerant per repo: a repo folder that is gone, or one whose remote was already
/// removed / renamed (a <see cref="RemoteNotFoundException"/> from the action), is tolerated and the loop
/// moves on — a half-broken machine must always finish cleaning.
/// </summary>
public sealed class SyncRemotePurger
{
    private readonly IReadOnlyList<string> _repoPaths;
    private readonly string _remoteName;
    private readonly Action<string, string> _removeRemote;

    public SyncRemotePurger(IReadOnlyList<string> repoPaths, string remoteName, Action<string, string> removeRemote)
    {
        _repoPaths = repoPaths ?? throw new ArgumentNullException(nameof(repoPaths));
        _remoteName = remoteName ?? throw new ArgumentNullException(nameof(remoteName));
        _removeRemote = removeRemote ?? throw new ArgumentNullException(nameof(removeRemote));
    }

    public SyncRemotePurgeReport Run(CancellationToken ct = default)
    {
        var removedFrom = new List<string>();
        var tolerated = 0;

        foreach (var path in _repoPaths)
        {
            ct.ThrowIfCancellationRequested();

            // A repo the user already deleted from disk: nothing to strip, tolerate and continue.
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                tolerated++;
                continue;
            }

            try
            {
                _removeRemote(path, _remoteName);
                removedFrom.Add(path);
            }
            catch (RemoteNotFoundException)
            {
                // The remote was never added, was already removed, or was renamed — tolerate it.
                tolerated++;
            }
            catch (ArgumentException)
            {
                // The path exists but is no longer a valid Git repository — tolerate it.
                tolerated++;
            }
        }

        return new SyncRemotePurgeReport(removedFrom.Count, tolerated, removedFrom);
    }
}
