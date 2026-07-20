using System;
using System.Collections.Generic;
using System.IO;

namespace Mainguard.Git.Services;

/// <summary>
/// The shipped <see cref="IRepoDiscoveryService"/>: mirrors the sidebar auto-detect scan
/// (<c>MainWindowViewModel.ScanAutoDetectFolderAsync</c>) — top-level directories of the chosen root
/// plus one level of subdirectories (a "workspaces" folder of grouping folders), each tested with
/// <see cref="IGitService.IsGitRepository"/>. Adds one convenience the picker needs: a root that is
/// itself a repository is returned as the single result instead of an empty scan of its innards.
/// Results are path-sorted for a stable list; unreadable directories are skipped, never thrown.
/// </summary>
public sealed class RepoDiscoveryService : IRepoDiscoveryService
{
    private readonly IGitService _git;

    public RepoDiscoveryService(IGitService? git = null)
    {
        _git = git ?? new GitService();
    }

    public IReadOnlyList<string> DiscoverRepositories(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        if (_git.IsGitRepository(rootPath))
        {
            return new[] { rootPath };
        }

        var found = new List<string>();
        foreach (var dir in SafeGetDirectories(rootPath))
        {
            if (_git.IsGitRepository(dir))
            {
                found.Add(dir);
                continue;
            }

            // Same one-extra-level descent the sidebar scan does (a grouping folder per
            // client/org whose children are the actual repositories).
            foreach (var sub in SafeGetDirectories(dir))
            {
                if (_git.IsGitRepository(sub))
                {
                    found.Add(sub);
                }
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    public bool IsGitRepository(string path) =>
        !string.IsNullOrWhiteSpace(path) && _git.IsGitRepository(path);

    private static string[] SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            // Access denied / reparse-point trouble: skip this branch, keep scanning the rest.
            return Array.Empty<string>();
        }
    }
}
