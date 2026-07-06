using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Git LFS operations (T-17). LFS is entirely a CLI concern — every method shells out through the
/// git CLI (never libgit2). Availability is probed once per repo (<c>git lfs version</c>) and cached;
/// every method first checks it and throws a typed
/// <see cref="Exceptions.GitOperationException"/> ("Git LFS is not installed.") rather than
/// attempting the op when LFS is absent. The one network op (<see cref="Pull"/>) goes through the
/// T-14 authenticated CLI path so a token never lands in argv/URL (G-4). The ls-files /
/// .gitattributes parsing is delegated to pure, unit-tested parsers.
/// </summary>
public interface ILfsService
{
    /// <summary>Cached probe of <c>git lfs version</c> for the repo (exit 0 → available).</summary>
    bool IsAvailable(string repoPath);

    /// <summary>True when LFS filters are configured in this repo's <b>local</b> config
    /// (i.e. <c>git lfs install --local</c> has run) — drives the per-repo enable toggle.</summary>
    bool IsEnabledForRepo(string repoPath);

    /// <summary>Enables LFS for this repository (<c>git lfs install --local</c>).</summary>
    void Install(string repoPath);

    /// <summary>Disables LFS filters for this repository (<c>git lfs uninstall --local</c>).</summary>
    void Uninstall(string repoPath);

    /// <summary>Tracks <paramref name="pattern"/> (<c>git lfs track</c>) — writes it into .gitattributes.</summary>
    void Track(string repoPath, string pattern);

    /// <summary>Stops tracking <paramref name="pattern"/> (<c>git lfs untrack</c>).</summary>
    void Untrack(string repoPath, string pattern);

    /// <summary>The LFS-tracked patterns from <c>.gitattributes</c> (filter=lfs), via the pure parser.</summary>
    IReadOnlyList<string> ListTrackedPatterns(string repoPath);

    /// <summary>The working-tree paths of the LFS objects (<c>git lfs ls-files</c>).</summary>
    IReadOnlyList<string> ListFiles(string repoPath);

    /// <summary>The LFS objects with their OID + downloaded/pointer status (<c>git lfs ls-files</c>).</summary>
    IReadOnlyList<LfsFile> ListLfsFiles(string repoPath);

    /// <summary>Downloads LFS objects for the current ref from the remote (<c>git lfs pull</c>).
    /// Runs through the authenticated CLI path — never a token in argv/URL.</summary>
    void Pull(string repoPath);

    /// <summary>
    /// Prunes old local LFS objects (<c>git lfs prune</c>; adds <c>--dry-run</c> when
    /// <paramref name="dryRun"/>). Returns git's summary line so the UI can show the dry-run result
    /// and confirm before the real prune.
    /// </summary>
    string Prune(string repoPath, bool dryRun);
}
