using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Analytics;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-22 — the T-22 analytics contract. The gitignore-aware language walk, cancellation, and the pure
/// punch-card / churn / contributor aggregators, pinned to exact numbers on scripted fixtures. Fixed
/// <see cref="DateTimeOffset"/>s throughout (never <c>Now</c>) so bucketing is deterministic on any CI.
/// </summary>
public class RepositoryAnalyzerTests
{
    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    private static LanguageModel? Lang(Dictionary<LanguageModel, long> d, string name)
        => d.Keys.FirstOrDefault(k => k.Name == name);

    // ---- Gitignore-aware language walk ---------------------------------------------------------

    [Fact]
    public async Task CalculateLanguageBreakdown_CountsExactlyNonIgnoredBytes_HonoringNegationAndSkippingGit()
    {
        using var fx = new TempRepoFixture();

        // *.js is ignored, but keep.js is negated back in; node_modules/ is ignored wholesale.
        fx.WriteFile(".gitignore", "node_modules/\n*.js\n!keep.js\n");

        const string appCs = "class App { }\n";      // C#, counted
        const string keepJs = "export const x = 1;\n"; // negated → counted
        fx.WriteFile("app.cs", appCs);
        fx.WriteFile("keep.js", keepJs);
        fx.WriteFile("bundle.js", "IGNORED BY *.js\n");            // ignored
        fx.WriteFile("node_modules/lib.js", "IGNORED IN DIR\n");   // ignored subtree

        // A source file physically inside .git must never be walked (explicit .git skip).
        File.WriteAllText(Path.Combine(fx.RepoPath, ".git", "hook.cs"), "SHOULD NOT COUNT");

        var analyzer = new RepositoryAnalyzer(new GitService());
        var result = await analyzer.CalculateLanguageBreakdownAsync(fx.RepoPath);

        Assert.Equal(Bytes(appCs), result[Lang(result, "C#")!]);
        Assert.Equal(Bytes(keepJs), result[Lang(result, "JavaScript")!]);
        Assert.Null(Lang(result, "Markdown")); // sanity: nothing spurious
        // Exactly two languages, exactly these byte totals — nothing from node_modules, bundle.js or .git.
        Assert.Equal(2, result.Count);
    }

    // ---- Cancellation --------------------------------------------------------------------------

    [Fact]
    public async Task CalculateLanguageBreakdown_HonorsCancellation_OnLargeTree()
    {
        using var fx = new TempRepoFixture();
        // A large synthetic tree the walk would otherwise churn through.
        for (int d = 0; d < 15; d++)
            for (int f = 0; f < 40; f++)
                fx.WriteFile($"src/pkg{d}/file{f}.cs", "public class C { }\n");

        var analyzer = new RepositoryAnalyzer(new GitService());
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled before start → the walk must return promptly, not enumerate the tree

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.CalculateLanguageBreakdownAsync(fx.RepoPath, cts.Token));
    }

    [Fact]
    public async Task CollectCommitStats_HonorsCancellation()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "one\n", "c1");

        var analyzer = new RepositoryAnalyzer(new GitService());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.CollectCommitStatsAsync(fx.RepoPath, cts.Token));
    }

    // ---- Per-HEAD commit-stats cache (Hotspot Register H1) --------------------------------------

    [Fact]
    public async Task CollectCommitStats_SameHead_IsServedFromCache_NewCommitMissesNaturally()
    {
        using var fx = new TempRepoFixture();
        var when = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 40; i++)
            fx.CommitFile($"src/f{i % 5}.cs", $"// rev {i}\nclass C{i} {{ }}\n", $"c{i}",
                "test-user", "test@gitloom.local", when.AddMinutes(i));

        // A private cache instance keeps this test deterministic under parallel test classes.
        var cache = new CommitStatsCache();
        var analyzer = new RepositoryAnalyzer(new GitService(), cache);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cold = await analyzer.CollectCommitStatsAsync(fx.RepoPath);
        var coldMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var cachedRun = await analyzer.CollectCommitStatsAsync(fx.RepoPath);
        var cachedMs = sw.Elapsed.TotalMilliseconds;

        // Same HEAD → the exact cached list comes back; no second walk happened.
        Assert.Same(cold, cachedRun);
        Assert.Equal(40, cold.Count);

        // A second analyzer instance sharing the cache also hits (the analytics tab is re-opened
        // with a fresh RepositoryAnalyzer each time — the cache must outlive the instance).
        var analyzer2 = new RepositoryAnalyzer(new GitService(), cache);
        Assert.Same(cold, await analyzer2.CollectCommitStatsAsync(fx.RepoPath));

        // HEAD moves → the key changes → natural miss, fresh walk, one more commit in the result.
        fx.CommitFile("src/new.cs", "class New { }\n", "c-new",
            "test-user", "test@gitloom.local", when.AddHours(2));
        var afterCommit = await analyzer.CollectCommitStatsAsync(fx.RepoPath);
        Assert.NotSame(cold, afterCommit);
        Assert.Equal(41, afterCommit.Count);

        // Measured H1 before/after on this fixture (cold walk vs per-HEAD cache hit).
        Console.WriteLine($"[H1] CollectCommitStats cold={coldMs:F1} ms, cached={cachedMs:F3} ms");
    }

    [Fact]
    public async Task CollectCommitStats_DifferentCap_IsADifferentCacheKey()
    {
        using var fx = new TempRepoFixture();
        var when = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 10; i++)
            fx.CommitFile("a.txt", $"rev {i}\n", $"c{i}", "test-user", "test@gitloom.local", when.AddMinutes(i));

        var cache = new CommitStatsCache();
        var analyzer = new RepositoryAnalyzer(new GitService(), cache);

        var capped = await analyzer.CollectCommitStatsAsync(fx.RepoPath, maxCommits: 5);
        var full = await analyzer.CollectCommitStatsAsync(fx.RepoPath);

        Assert.Equal(5, capped.Count);
        Assert.Equal(10, full.Count);
        Assert.NotSame(capped, full); // the cap is part of the key — a capped walk never masquerades as the full one
    }

    [Fact]
    public void CommitStatsCache_IsBounded_EvictsLeastRecentlyUsed()
    {
        var cache = new CommitStatsCache(capacity: 2);
        var a = new CommitStatsCache.Key("/r", "aaa", 100);
        var b = new CommitStatsCache.Key("/r", "bbb", 100);
        var c = new CommitStatsCache.Key("/r", "ccc", 100);
        var stats = new[] { Commit(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)) };

        cache.Set(a, stats);
        cache.Set(b, stats);
        Assert.True(cache.TryGet(a, out _)); // touch a → b becomes LRU
        cache.Set(c, stats);                 // evicts b, never grows past capacity

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(a, out _));
        Assert.False(cache.TryGet(b, out _));
        Assert.True(cache.TryGet(c, out _));
    }

    [Fact]
    public void CommitStatsCache_InvalidateRepo_DropsOnlyThatRepo()
    {
        var cache = new CommitStatsCache();
        var stats = new[] { Commit(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)) };
        cache.Set(new CommitStatsCache.Key("/repo-1", "aaa", 100), stats);
        cache.Set(new CommitStatsCache.Key("/repo-1", "bbb", 100), stats);
        cache.Set(new CommitStatsCache.Key("/repo-2", "aaa", 100), stats);

        cache.InvalidateRepo("/repo-1");

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(new CommitStatsCache.Key("/repo-2", "aaa", 100), out _));
    }

    // ---- Punch-card bucketing (pure, exact) ----------------------------------------------------

    [Fact]
    public void PunchCard_BucketsByOffsetWallClock_Exact()
    {
        // 2026-01-05 is a Monday. DayOfWeek: Sunday=0 … Saturday=6.
        var commits = new[]
        {
            Commit(new DateTimeOffset(2026, 1, 5, 14, 30, 0, TimeSpan.Zero)),      // Mon 14:00
            Commit(new DateTimeOffset(2026, 1, 5, 14, 59, 0, TimeSpan.Zero)),      // Mon 14:00
            Commit(new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero)),        // Sun 09:00
            Commit(new DateTimeOffset(2026, 1, 5, 14, 0, 0, TimeSpan.FromHours(5))), // Mon 14:00 at +05 offset
        };

        var card = PunchCardStats.FromCommits(commits);

        Assert.Equal(3, card.CommitsByDayHour[1, 14]); // Monday 14:00 (offset wall-clock, not UTC)
        Assert.Equal(1, card.CommitsByDayHour[0, 9]);  // Sunday 09:00
        Assert.Equal(4, card.TotalCommits);
        Assert.Equal(3, card.PeakCount);
    }

    // ---- Churn bucketing (pure, exact) ---------------------------------------------------------

    [Fact]
    public void Churn_BucketsByWeek_ZeroFills_ExcludesMerges()
    {
        var commits = new[]
        {
            Commit(new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero), added: 10, removed: 2), // wk 01-05
            Commit(new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero), added: 5, removed: 1),  // wk 01-05
            Commit(new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero), added: 100, removed: 50), // wk 01-19
            Commit(new DateTimeOffset(2026, 1, 21, 0, 0, 0, TimeSpan.Zero), added: 999, removed: 999, parents: 2), // merge → excluded
        };

        var churn = ChurnStats.FromCommits(commits);

        Assert.Equal(3, churn.Weeks.Count); // 01-05, 01-12 (zero-filled), 01-19
        Assert.Equal(new DateOnly(2026, 1, 5), churn.Weeks[0].WeekStart);
        Assert.Equal(15, churn.Weeks[0].Added);
        Assert.Equal(3, churn.Weeks[0].Removed);
        Assert.Equal(new DateOnly(2026, 1, 12), churn.Weeks[1].WeekStart);
        Assert.Equal(0, churn.Weeks[1].Total); // zero-filled gap week
        Assert.Equal(100, churn.Weeks[2].Added);
        Assert.Equal(50, churn.Weeks[2].Removed);
        Assert.Equal(115, churn.TotalAdded); // merge's 999 never counted
        Assert.Equal(53, churn.TotalRemoved);
    }

    // ---- Contributor breakdown (pure, exact) ---------------------------------------------------

    [Fact]
    public void Contributors_MergeByEmailCaseInsensitive_RankByCommits()
    {
        var commits = new[]
        {
            Commit(new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero), name: "Ada", email: "ada@x.io", added: 10, removed: 1),
            Commit(new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero), name: "Ada L", email: "ADA@x.io", added: 4, removed: 0),
            Commit(new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero), name: "Bob", email: "bob@x.io", added: 2, removed: 2),
            Commit(new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero), name: "Ada L", email: "ada@x.io", added: 999, removed: 999, parents: 2), // merge: counts as a commit, no lines
        };

        var stats = ContributorStats.FromCommits(commits);

        Assert.Equal(2, stats.Count);
        Assert.Equal("ada@x.io", stats[0].Email);
        Assert.Equal(3, stats[0].Commits);        // two normal + one merge
        Assert.Equal(14, stats[0].LinesAdded);    // merge lines excluded (10 + 4)
        Assert.Equal("Ada L", stats[0].Name);     // newest author-name for the identity
        Assert.Equal("bob@x.io", stats[1].Email);
        Assert.Equal(1, stats[1].Commits);
    }

    // ---- Integration: the walk feeds the aggregators correctly ---------------------------------

    [Fact]
    public async Task CollectCommitStats_ReportsChurnAndTimestamps_FromFixedTimestamps()
    {
        using var fx = new TempRepoFixture();
        var when1 = new DateTimeOffset(2026, 1, 6, 10, 0, 0, TimeSpan.Zero);
        var when2 = new DateTimeOffset(2026, 1, 6, 11, 0, 0, TimeSpan.Zero);
        fx.CommitFile("a.txt", "l1\nl2\nl3\n", "root", "Ada", "ada@x.io", when1);           // +3 lines (vs empty tree)
        fx.CommitFile("a.txt", "l1\nl2\nl3\nl4\n", "grow", "Ada", "ada@x.io", when2);        // +1 line

        var analyzer = new RepositoryAnalyzer(new GitService());
        var stats = await analyzer.CollectCommitStatsAsync(fx.RepoPath);

        Assert.Equal(2, stats.Count);
        Assert.All(stats, s => Assert.False(s.IsMerge));
        var churn = ChurnStats.FromCommits(stats);
        Assert.Equal(4, churn.TotalAdded);   // 3 (root) + 1
        Assert.Equal(0, churn.TotalRemoved);

        var card = PunchCardStats.FromCommits(stats);
        Assert.Equal(2, card.CommitsByDayHour[2, 10] + card.CommitsByDayHour[2, 11]); // Tuesday 10:00 & 11:00
    }

    [Fact]
    public async Task CollectCommitStats_ExcludesBinaryChurn()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "l1\nl2\n", "root",
            "Ada", "ada@x.io", new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero));

        // A binary blob: NUL bytes make git treat it as binary → 0 line churn.
        File.WriteAllBytes(Path.Combine(fx.RepoPath, "img.bin"), new byte[] { 0, 1, 2, 0, 3, 4, 0 });
        using (var repo = new Repository(fx.RepoPath))
        {
            Commands.Stage(repo, "img.bin");
            var sig = new Signature("Ada", "ada@x.io", new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero));
            repo.Commit("add binary", sig, sig);
        }

        var analyzer = new RepositoryAnalyzer(new GitService());
        var churn = ChurnStats.FromCommits(await analyzer.CollectCommitStatsAsync(fx.RepoPath));

        Assert.Equal(2, churn.TotalAdded); // only the 2 text lines; binary contributes nothing
    }

    [Fact]
    public async Task CollectCommitStats_FlagsMergeCommit_WithZeroChurn()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("base.txt", "base\n", "root");
        string trunk;
        using (var repo = new Repository(fx.RepoPath)) trunk = repo.Head.FriendlyName;
        fx.CreateBranch("feature");
        fx.Checkout("feature");
        fx.CommitFile("feature.txt", "f1\nf2\n", "feature work");
        fx.Checkout(trunk);
        fx.CommitFile("main.txt", "m1\n", "main work");

        using (var repo = new Repository(fx.RepoPath))
        {
            var sig = new Signature("Merger", "merge@x.io", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
            repo.Merge(repo.Branches["feature"], sig,
                new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
        }

        var analyzer = new RepositoryAnalyzer(new GitService());
        var stats = await analyzer.CollectCommitStatsAsync(fx.RepoPath);

        var merge = Assert.Single(stats, s => s.IsMerge);
        Assert.Equal(2, merge.ParentCount);
        Assert.Equal(0, merge.LinesAdded);
        Assert.Equal(0, merge.LinesRemoved);
    }

    // ---- Edge cases: empty repo, single commit, capped history ---------------------------------

    [Fact]
    public async Task EmptyRepo_YieldsEmptyStats()
    {
        using var fx = new TempRepoFixture(); // no commits, no source files
        var analyzer = new RepositoryAnalyzer(new GitService());

        var stats = await analyzer.CollectCommitStatsAsync(fx.RepoPath);
        Assert.Empty(stats);
        Assert.Equal(0, PunchCardStats.FromCommits(stats).TotalCommits);
        Assert.Empty(ChurnStats.FromCommits(stats).Weeks);
        Assert.Empty(ContributorStats.FromCommits(stats));

        var langs = await analyzer.CalculateLanguageBreakdownAsync(fx.RepoPath);
        Assert.Empty(langs);
    }

    [Fact]
    public async Task SingleCommit_YieldsSingleWeekAndCell()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "x\n", "only",
            "Ada", "ada@x.io", new DateTimeOffset(2026, 1, 6, 8, 0, 0, TimeSpan.Zero));

        var analyzer = new RepositoryAnalyzer(new GitService());
        var stats = await analyzer.CollectCommitStatsAsync(fx.RepoPath);

        Assert.Single(stats);
        Assert.Single(ChurnStats.FromCommits(stats).Weeks);
        var card = PunchCardStats.FromCommits(stats);
        Assert.Equal(1, card.TotalCommits);
        Assert.Single(card.GetDataPoints());
    }

    [Fact]
    public async Task CommitCap_LimitsHistoryWalk()
    {
        using var fx = new TempRepoFixture();
        for (int i = 0; i < 5; i++) fx.CommitFile("a.txt", $"line {i}\n", $"c{i}");

        var analyzer = new RepositoryAnalyzer(new GitService());
        var stats = await analyzer.CollectCommitStatsAsync(fx.RepoPath, maxCommits: 3);

        Assert.Equal(3, stats.Count);
    }

    private static CommitStat Commit(DateTimeOffset when, string name = "Dev", string email = "dev@x.io",
        long added = 0, long removed = 0, int parents = 1)
        => new(when, name, email, added, removed, parents);
}
