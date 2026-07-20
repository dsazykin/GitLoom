using System.Collections.Generic;

namespace Mainguard.Git.Services;

/// <summary>
/// Local git-repository discovery — the service form of the sidebar's auto-detect folder scan
/// (<c>UserPreferences.AutoDetectPath</c>), extracted so the OOBE repo-onboarding step and tests can
/// consume the same behaviour through a seam. Detection is <see cref="IGitService.IsGitRepository"/>;
/// this interface adds only the directory walk on top of it.
/// </summary>
public interface IRepoDiscoveryService
{
    /// <summary>
    /// Finds git repositories under <paramref name="rootPath"/> using the same shape as the sidebar
    /// auto-detect scan: the root itself when it is a repository, otherwise its immediate
    /// subdirectories plus one further level down (the "one folder of project folders" layout).
    /// A missing/unreadable directory yields an empty list — discovery never throws for that.
    /// </summary>
    IReadOnlyList<string> DiscoverRepositories(string rootPath);

    /// <summary>Whether one specific path is a git repository (the individual-pick validation) —
    /// a passthrough to <see cref="IGitService.IsGitRepository"/> so callers need only this seam.</summary>
    bool IsGitRepository(string path);
}
