using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Services;
using LibGit2Sharp;

namespace GitLoom.Core.Analytics;

/// <summary>
/// Repository analytics source. Two background walks feed the analytics view:
/// <list type="bullet">
/// <item>a gitignore-aware working-tree walk → bytes per language (like github-linguist);</item>
/// <item>a single history walk → <see cref="CommitStat"/>s, which the pure aggregators
/// (<see cref="PunchCardStats"/>, <see cref="ChurnStats"/>, <see cref="ContributorStats"/>) turn into
/// the punch-card, churn and contributor series.</item>
/// </list>
/// All libgit2 access goes through <see cref="IGitService.ExecuteWithRepo"/> (handle discipline), and
/// both walks honour a <see cref="CancellationToken"/> so a repo switch cancels an in-flight analysis
/// promptly. The aggregation lives in the pure types above, not here, so the numbers are unit-tested.
/// </summary>
public class RepositoryAnalyzer
{
    private const int DefaultCommitCap = 10_000;

    private readonly IGitService _git;

    public RepositoryAnalyzer(IGitService? git = null)
    {
        _git = git ?? new GitService();
    }

    /// <summary>Bytes per recognized language across the non-ignored working tree.</summary>
    public Task<Dictionary<LanguageModel, long>> CalculateLanguageBreakdownAsync(
        string repositoryPath, CancellationToken ct = default)
    {
        return Task.Run(() => _git.ExecuteWithRepo(repositoryPath, repo =>
        {
            var languageBytes = new Dictionary<LanguageModel, long>();
            var workdir = repo.Info.WorkingDirectory;
            if (string.IsNullOrEmpty(workdir)) return languageBytes; // bare repo — nothing to walk

            // Per-directory ignore decisions are cached so we evaluate each directory once, not per file.
            var ignoreCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            WalkLanguages(repo, new DirectoryInfo(workdir), workdir, languageBytes, ignoreCache, ct);
            return languageBytes;
        }), ct);
    }

    private static void WalkLanguages(
        Repository repo, DirectoryInfo dir, string workdir,
        Dictionary<LanguageModel, long> languageBytes,
        Dictionary<string, bool> ignoreCache, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var sub in dir.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();

            // The .git directory is never analyzed, ignore rules notwithstanding.
            if (string.Equals(sub.Name, ".git", StringComparison.Ordinal)) continue;
            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // don't follow symlinked dirs

            var rel = RelativePath(workdir, sub.FullName) + "/";
            if (IsIgnored(repo, rel, ignoreCache)) continue; // whole subtree ignored — skip descent

            WalkLanguages(repo, sub, workdir, languageBytes, ignoreCache, ct);
        }

        int seen = 0;
        foreach (var file in dir.EnumerateFiles())
        {
            if ((++seen & 0x3F) == 0) ct.ThrowIfCancellationRequested(); // periodic check on big trees

            var rel = RelativePath(workdir, file.FullName);
            if (IsIgnored(repo, rel, ignoreCache)) continue; // honors negations (!keep.js) via libgit2

            var lang = LanguageRegistry.GetLanguageByExtension(file.Extension);
            if (lang == null) continue;

            long size;
            try { size = file.Length; }
            catch { continue; } // vanished mid-walk — ignore

            languageBytes.TryGetValue(lang, out var acc);
            languageBytes[lang] = acc + size;
        }
    }

    private static bool IsIgnored(Repository repo, string relPath, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(relPath, out var cached)) return cached;
        var ignored = repo.Ignore.IsPathIgnored(relPath);
        cache[relPath] = ignored;
        return ignored;
    }

    private static string RelativePath(string workdir, string fullPath)
        => Path.GetRelativePath(workdir, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Single history walk from HEAD (capped) → one <see cref="CommitStat"/> per commit. Line churn is
    /// the diff vs the first parent (root commit vs the empty tree); merges get 0 churn (flagged by
    /// <see cref="CommitStat.ParentCount"/>) so branch work is not double-counted; binary files report
    /// 0 lines and so drop out of churn naturally.
    /// </summary>
    public Task<IReadOnlyList<CommitStat>> CollectCommitStatsAsync(
        string repositoryPath, CancellationToken ct = default, int maxCommits = DefaultCommitCap)
    {
        return Task.Run<IReadOnlyList<CommitStat>>(() => _git.ExecuteWithRepo(repositoryPath, repo =>
        {
            var result = new List<CommitStat>();
            if (repo.Head.Tip == null) return result; // unborn / empty repo

            foreach (var commit in repo.Commits.Take(maxCommits))
            {
                ct.ThrowIfCancellationRequested();

                int parentCount = commit.Parents.Count();
                long added = 0, removed = 0;
                if (parentCount <= 1) // skip merge diffs; root diffs against the empty tree
                {
                    var parentTree = commit.Parents.FirstOrDefault()?.Tree;
                    var stats = repo.Diff.Compare<PatchStats>(parentTree, commit.Tree);
                    added = stats.TotalLinesAdded;
                    removed = stats.TotalLinesDeleted;
                }

                result.Add(new CommitStat(
                    commit.Author.When,
                    commit.Author.Name ?? string.Empty,
                    commit.Author.Email ?? string.Empty,
                    added, removed, parentCount));
            }

            return result;
        }), ct);
    }

    /// <summary>Convenience: collect commit stats and fold them into a punch card.</summary>
    public async Task<PunchCardStats> GeneratePunchCardAsync(string repositoryPath, CancellationToken ct = default)
        => PunchCardStats.FromCommits(await CollectCommitStatsAsync(repositoryPath, ct).ConfigureAwait(false));
}
