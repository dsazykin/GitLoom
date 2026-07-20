using System;
using System.Collections.Generic;
using System.IO;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// CLI-driven Git LFS service (T-17). Availability is probed once per repo (<c>git lfs version</c>)
/// and cached; every method calls <see cref="IsAvailable"/> first and throws a typed
/// <see cref="GitOperationException"/> rather than attempting the op when LFS is absent (TI-17 #4).
/// Local ops run through the plain checked runner; <see cref="Pull"/> (a network op) runs through the
/// T-14 authenticated path on <see cref="GitService"/> so a token never lands in argv/URL (G-4).
/// LfsService composes <see cref="GitService"/> deliberately: the security-sensitive authenticated CLI
/// path lives in exactly one audited place rather than being duplicated here. The ls-files /
/// .gitattributes parsing is delegated to the pure parsers so it is unit-testable off IO.
/// </summary>
public sealed class LfsService : ILfsService
{
    private readonly GitService _git;
    private readonly Dictionary<string, bool> _availability = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    // Test seam: when set, short-circuits the real probe (e.g. simulate git-lfs absent) so the
    // "degrade gracefully" test can run on a machine that HAS git-lfs. Never set in production.
    internal bool? AvailabilityOverride { get; set; }

    public LfsService(GitService git) => _git = git;

    public bool IsAvailable(string repoPath)
    {
        if (AvailabilityOverride is { } forced) return forced;

        lock (_gate)
        {
            if (_availability.TryGetValue(repoPath, out var cached)) return cached;
            var (code, _, _) = GitService.RunGit(repoPath, "lfs", "version");
            var available = code == 0;
            _availability[repoPath] = available;
            return available;
        }
    }

    // Never attempt an op when LFS is unavailable — throw the typed error first (TI-17 #4).
    private void EnsureAvailable(string repoPath)
    {
        if (!IsAvailable(repoPath))
            throw new GitOperationException("Git LFS is not installed.");
    }

    public bool IsEnabledForRepo(string repoPath)
    {
        EnsureAvailable(repoPath);
        // `git lfs install --local` writes filter.lfs.* into the repo's LOCAL config.
        var (code, _, _) = GitService.RunGit(repoPath, "config", "--local", "--get", "filter.lfs.smudge");
        return code == 0;
    }

    public void Install(string repoPath)
    {
        EnsureAvailable(repoPath);
        _git.RunGitCheckedForLfs(repoPath, "lfs", "install", "--local");
    }

    public void Uninstall(string repoPath)
    {
        EnsureAvailable(repoPath);
        _git.RunGitCheckedForLfs(repoPath, "lfs", "uninstall", "--local");
    }

    public void Track(string repoPath, string pattern)
    {
        EnsureAvailable(repoPath);
        _git.RunGitCheckedForLfs(repoPath, "lfs", "track", pattern);
    }

    public void Untrack(string repoPath, string pattern)
    {
        EnsureAvailable(repoPath);
        _git.RunGitCheckedForLfs(repoPath, "lfs", "untrack", pattern);
    }

    public IReadOnlyList<string> ListTrackedPatterns(string repoPath)
    {
        EnsureAvailable(repoPath);
        var path = Path.Combine(repoPath, ".gitattributes");
        if (!File.Exists(path)) return Array.Empty<string>();
        return LfsAttributesParser.Parse(File.ReadAllText(path));
    }

    public IReadOnlyList<LfsFile> ListLfsFiles(string repoPath)
    {
        EnsureAvailable(repoPath);
        var (code, output, err) = GitService.RunGit(repoPath, "lfs", "ls-files");
        if (code != 0)
            throw new GitOperationException(string.IsNullOrWhiteSpace(err)
                ? $"git lfs ls-files failed with exit code {code}." : err);
        return LfsLsFilesParser.Parse(output);
    }

    public IReadOnlyList<string> ListFiles(string repoPath)
    {
        var files = ListLfsFiles(repoPath);
        var paths = new List<string>(files.Count);
        foreach (var f in files) paths.Add(f.Path);
        return paths;
    }

    public void Pull(string repoPath)
    {
        EnsureAvailable(repoPath);
        // Network op → authenticated CLI path (token via env, never argv/URL — T-14/G-4).
        _git.RunGitAuthenticatedForLfs(repoPath, "lfs", "pull");
    }

    public string Prune(string repoPath, bool dryRun)
    {
        EnsureAvailable(repoPath);
        var (code, output, err) = dryRun
            ? GitService.RunGit(repoPath, "lfs", "prune", "--dry-run")
            : GitService.RunGit(repoPath, "lfs", "prune");
        if (code != 0)
            throw new GitOperationException(string.IsNullOrWhiteSpace(err)
                ? $"git lfs prune failed with exit code {code}." : err);
        // prune prints its summary to stdout ("prune: N local objects, N retained, done."); some
        // builds route progress to stderr, so fall back to it when stdout is empty.
        var summary = string.IsNullOrWhiteSpace(output) ? err : output;
        return summary.Trim();
    }
}
